using System.Globalization;
using System.Linq.Expressions;
using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Core.Rules;
using Desk.Core.Tenancy;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Endpoints;

/// <summary>
/// Knowledge base (US-17, US-18). Tenant isolation comes from the global query filter: an article of another company is
/// simply not found (404), on reads and on writes alike. The search text only ever travels as query parameters.
/// </summary>
public static class ArticleEndpoints
{
    private const string LikeEscape = "\\";

    public static IEndpointRouteBuilder MapArticleEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/articles").WithTags("articles");

        group.MapGet("/", List).WithName("listArticles").RequireAuthorization(Policies.TenantMember);
        group.MapPost("/", Create).WithName("createArticle").RequireAuthorization(Policies.Agent)
            .AddEndpointFilter<ValidationFilter<ArticleRequest>>();
        group.MapGet("/{id:guid}", Get).WithName("getArticle").RequireAuthorization(Policies.TenantMember);
        group.MapPut("/{id:guid}", Update).WithName("updateArticle").RequireAuthorization(Policies.Agent)
            .AddEndpointFilter<ValidationFilter<ArticleRequest>>();
        group.MapPost("/{id:guid}/publish", Publish).WithName("publishArticle").RequireAuthorization(Policies.Agent);
        group.MapPost("/{id:guid}/unpublish", Unpublish).WithName("unpublishArticle").RequireAuthorization(Policies.Agent);
        return app;
    }

    private static bool IsStaff(HttpContext http) => http.User.RoleOf() is Role.Agent or Role.TenantAdmin;

    private static DateTimeOffset Now(TimeProvider clock)
    {
        // PostgreSQL keeps microseconds: truncate so that what we return equals what is stored.
        var now = clock.GetUtcNow();
        return new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);
    }

    private static async Task<IResult> List(
        HttpContext http, DeskDbContext db, CancellationToken ct,
        string? q = null, string? status = null, string? page = null, string? pageSize = null)
    {
        var errors = new Dictionary<string, string[]>();
        var staff = IsStaff(http);

        ArticleStatus? wanted = null;
        // A Requester only ever sees published articles: his status filter is ignored, not validated.
        if (staff && status is not null)
        {
            if (EnumNames.TryParse<ArticleStatus>(status, out var parsed)) { wanted = parsed; }
            else { errors["status"] = ["Statut invalide : Draft ou Published."]; }
        }

        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        if (search is { Length: > 100 })
        {
            errors["q"] = ["q ne doit pas dépasser 100 caractères."];
        }

        var pageNumber = 1;
        if (page is not null && (!int.TryParse(page, NumberStyles.None, CultureInfo.InvariantCulture, out pageNumber) || pageNumber < 1))
        {
            errors["page"] = ["page doit être un entier supérieur ou égal à 1."];
        }

        var size = 20;
        if (pageSize is not null
            && (!int.TryParse(pageSize, NumberStyles.None, CultureInfo.InvariantCulture, out size) || size is < 1 or > PagingExtensions.MaxPageSize))
        {
            errors["pageSize"] = [$"pageSize doit être compris entre 1 et {PagingExtensions.MaxPageSize}."];
        }

        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors, statusCode: 422, title: "Paramètres invalides");
        }

        IQueryable<KnowledgeArticle> articles = db.KnowledgeArticles.AsNoTracking();
        if (!staff)
        {
            articles = articles.Where(a => a.Status == ArticleStatus.Published);
        }
        else if (wanted is { } only)
        {
            articles = articles.Where(a => a.Status == only);
        }

        var words = ArticleRules.SearchWords(search);
        Expression<Func<KnowledgeArticle, bool>>? titleHasAll = null;
        foreach (var word in words)
        {
            var pattern = $"%{ArticleRules.EscapeLike(word)}%";
            // Case and accents are ignored on both sides (unaccent + ILIKE, ADR 010); the escape character stays literal.
            articles = articles.Where(a =>
                EF.Functions.ILike(DeskDbFunctions.Unaccent(a.Title), DeskDbFunctions.Unaccent(pattern), LikeEscape)
                || EF.Functions.ILike(DeskDbFunctions.Unaccent(a.Symptom), DeskDbFunctions.Unaccent(pattern), LikeEscape)
                || EF.Functions.ILike(DeskDbFunctions.Unaccent(a.Cause), DeskDbFunctions.Unaccent(pattern), LikeEscape)
                || EF.Functions.ILike(DeskDbFunctions.Unaccent(a.Solution), DeskDbFunctions.Unaccent(pattern), LikeEscape)
                || EF.Functions.ILike(DeskDbFunctions.Unaccent(a.Tags), DeskDbFunctions.Unaccent(pattern), LikeEscape));

            Expression<Func<KnowledgeArticle, bool>> inTitle = a =>
                EF.Functions.ILike(DeskDbFunctions.Unaccent(a.Title), DeskDbFunctions.Unaccent(pattern), LikeEscape);
            titleHasAll = titleHasAll is null ? inTitle : AndAlso(titleHasAll, inTitle);
        }

        IOrderedQueryable<KnowledgeArticle> ordered;
        if (titleHasAll is not null)
        {
            // Articles whose title holds every word come first.
            var rank = Expression.Lambda<Func<KnowledgeArticle, int>>(
                Expression.Condition(titleHasAll.Body, Expression.Constant(0), Expression.Constant(1)), titleHasAll.Parameters);
            ordered = articles.OrderBy(rank).ThenBy(a => a.Status == ArticleStatus.Published ? 0 : 1);
        }
        else
        {
            ordered = articles.OrderBy(a => a.Status == ArticleStatus.Published ? 0 : 1);
        }

        // Published by recency, then drafts by creation date (their PublishedAt is null).
        ordered = ordered.ThenByDescending(a => a.PublishedAt).ThenByDescending(a => a.CreatedAt).ThenBy(a => a.Id);
        return Results.Ok(await ordered.ToPageAsync(pageNumber, size, ArticleSummaryDto.From, ct));
    }

    private static Expression<Func<KnowledgeArticle, bool>> AndAlso(
        Expression<Func<KnowledgeArticle, bool>> left, Expression<Func<KnowledgeArticle, bool>> right)
    {
        var rebound = new ParameterRebinder(right.Parameters[0], left.Parameters[0]).Visit(right.Body);
        return Expression.Lambda<Func<KnowledgeArticle, bool>>(Expression.AndAlso(left.Body, rebound), left.Parameters);
    }

    private sealed class ParameterRebinder(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) => node == from ? to : base.VisitParameter(node);
    }

    private static async Task<IResult> Get(Guid id, HttpContext http, DeskDbContext db, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
        if (article is null || (article.Status == ArticleStatus.Draft && !IsStaff(http)))
        {
            return Problems.NotFound();
        }

        return Results.Ok(ArticleDto.From(article, await AuthorAsync(db, article.AuthorId, ct)));
    }

    private static async Task<IResult> Create(
        ArticleRequest request, HttpContext http, DeskDbContext db, ITenantContext tenant, TimeProvider clock, CancellationToken ct)
    {
        if (tenant.TenantId is not { } tenantId)
        {
            return Problems.Forbidden("Aucune entreprise associée à ce compte.");
        }

        var article = new KnowledgeArticle
        {
            TenantId = tenantId,
            Status = ArticleStatus.Draft,
            AuthorId = http.User.UserId(),
            CreatedAt = Now(clock),
        };
        Apply(article, request);
        db.KnowledgeArticles.Add(article);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/api/v1/articles/{article.Id}", ArticleDto.From(article, await AuthorAsync(db, article.AuthorId, ct)));
    }

    private static async Task<IResult> Update(Guid id, ArticleRequest request, DeskDbContext db, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (article is null)
        {
            return Problems.NotFound();
        }

        // A published article stays complete: its symptom and solution cannot be emptied (a draft is free).
        if (article.Status == ArticleStatus.Published)
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(request.Symptom)) { errors["symptom"] = ["Le symptôme d'un article publié ne peut pas être vide."]; }
            if (string.IsNullOrWhiteSpace(request.Solution)) { errors["solution"] = ["La solution d'un article publié ne peut pas être vide."]; }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: 422, title: "Données invalides");
            }
        }

        // Last write wins (no rowVersion in the contract); the status, author and source ticket never change here.
        Apply(article, request);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ArticleDto.From(article, await AuthorAsync(db, article.AuthorId, ct)));
    }

    private static async Task<IResult> Publish(Guid id, DeskDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (article is null)
        {
            return Problems.NotFound();
        }

        if (article.Status != ArticleStatus.Published)
        {
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(article.Title)) { errors["title"] = ["Le titre est requis pour publier."]; }
            if (string.IsNullOrWhiteSpace(article.Symptom)) { errors["symptom"] = ["Le symptôme est requis pour publier."]; }
            if (string.IsNullOrWhiteSpace(article.Solution)) { errors["solution"] = ["La solution est requise pour publier."]; }
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: 422, title: "Données invalides");
            }

            article.Status = ArticleStatus.Published;
            article.PublishedAt = Now(clock);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ArticleDto.From(article, await AuthorAsync(db, article.AuthorId, ct)));
    }

    private static async Task<IResult> Unpublish(Guid id, DeskDbContext db, CancellationToken ct)
    {
        var article = await db.KnowledgeArticles.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (article is null)
        {
            return Problems.NotFound();
        }

        if (article.Status != ArticleStatus.Draft)
        {
            article.Status = ArticleStatus.Draft;
            article.PublishedAt = null;
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ArticleDto.From(article, await AuthorAsync(db, article.AuthorId, ct)));
    }

    private static void Apply(KnowledgeArticle article, ArticleRequest request)
    {
        article.Title = request.Title!.Trim();
        article.Symptom = request.Symptom!.Trim();
        article.Cause = request.Cause!.Trim();
        article.Solution = request.Solution!.Trim();
        article.Tags = ArticleRules.JoinTags(ArticleRules.NormalizeTags(request.Tags!.Select(t => t!)));
    }

    /// <summary>The author stays displayed when his account is disabled (only the tenant filter applies).</summary>
    private static async Task<UserRefDto> AuthorAsync(DeskDbContext db, Guid authorId, CancellationToken ct) =>
        await db.Users.AsNoTracking().Where(u => u.Id == authorId).Select(u => new UserRefDto(u.Id, u.DisplayName)).FirstOrDefaultAsync(ct)
        ?? new UserRefDto(authorId, "");
}
