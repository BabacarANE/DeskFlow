using Desk.Core.Domain;
using Desk.Core.Rules;

namespace Desk.Api.Contracts;

public sealed record ArticleSummaryDto(
    Guid Id, string Title, ArticleStatus Status, IReadOnlyList<string> Tags, string Excerpt, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt)
{
    public static ArticleSummaryDto From(KnowledgeArticle a) =>
        new(a.Id, a.Title, a.Status, ArticleRules.SplitTags(a.Tags), ArticleRules.Excerpt(a.Symptom), a.CreatedAt, a.PublishedAt);
}

public sealed record ArticleDto(
    Guid Id, string Title, string Symptom, string Cause, string Solution, IReadOnlyList<string> Tags, ArticleStatus Status,
    Guid? SourceTicketId, UserRefDto Author, DateTimeOffset CreatedAt, DateTimeOffset? PublishedAt)
{
    public static ArticleDto From(KnowledgeArticle a, UserRefDto author) =>
        new(a.Id, a.Title, a.Symptom, a.Cause, a.Solution, ArticleRules.SplitTags(a.Tags), a.Status, a.SourceTicketId, author, a.CreatedAt, a.PublishedAt);
}

public sealed record ArticleRequest(string? Title, string? Symptom, string? Cause, string? Solution, List<string?>? Tags);

public sealed record ThemeRequest(string? ColorPrimary, string? ColorSurface, string? ColorAccent);

public sealed record ThemeUpdateResultDto(ThemeDto Theme, IReadOnlyList<string> Warnings);

public sealed record SlaPolicyDto(Priority Priority, int FirstResponseMinutes, int ResolutionMinutes)
{
    public static SlaPolicyDto From(SlaPolicy p) => new(p.Priority, p.FirstResponseMinutes, p.ResolutionMinutes);
}

public sealed record SlaPolicyRequest(string? Priority, int? FirstResponseMinutes, int? ResolutionMinutes);

public sealed record UpdateSlaPoliciesRequest(List<SlaPolicyRequest?>? Policies);
