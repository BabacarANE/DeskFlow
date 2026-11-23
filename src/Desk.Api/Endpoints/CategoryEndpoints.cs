using Desk.Api.Contracts;
using Desk.Api.Http;
using Desk.Core.Domain;
using Desk.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Desk.Api.Endpoints;

/// <summary>Categories of the current tenant (the global filter hides the others: foreign ids answer 404).</summary>
public static class CategoryEndpoints
{
    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/categories").WithTags("categories");

        group.MapGet("/", List).WithName("listCategories").RequireAuthorization();
        group.MapPost("/", Create).WithName("createCategory").RequireAuthorization(Policies.TenantAdmin)
            .AddEndpointFilter<ValidationFilter<CategoryRequest>>();
        group.MapPut("/{id:guid}", Update).WithName("updateCategory").RequireAuthorization(Policies.TenantAdmin)
            .AddEndpointFilter<ValidationFilter<CategoryRequest>>();
        group.MapDelete("/{id:guid}", Delete).WithName("deleteCategory").RequireAuthorization(Policies.TenantAdmin);
        return app;
    }

    private static async Task<IResult> List(DeskDbContext db, CancellationToken ct)
    {
        var categories = await db.Categories.AsNoTracking().OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(ct);
        return Results.Ok(categories.Select(c => new CategoryDto(c.Id, c.Name)));
    }

    private static async Task<IResult> Create(CategoryRequest request, DeskDbContext db, CancellationToken ct)
    {
        var name = request.Name!.Trim();
        if (await NameTakenAsync(db, name, null, ct))
        {
            return DuplicateName();
        }

        var category = new Category { Name = name };
        db.Categories.Add(category);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return DuplicateName();
        }

        return Results.Created($"/api/v1/categories/{category.Id}", new CategoryDto(category.Id, category.Name));
    }

    private static async Task<IResult> Update(Guid id, CategoryRequest request, DeskDbContext db, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return Problems.NotFound();
        }

        var name = request.Name!.Trim();
        if (await NameTakenAsync(db, name, id, ct))
        {
            return DuplicateName();
        }

        category.Name = name;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (IsUniqueViolation(e))
        {
            return DuplicateName();
        }

        return Results.Ok(new CategoryDto(category.Id, category.Name));
    }

    private static async Task<IResult> Delete(Guid id, DeskDbContext db, CancellationToken ct)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (category is null)
        {
            return Problems.NotFound();
        }

        if (await db.Tickets.AnyAsync(t => t.CategoryId == id, ct))
        {
            return InUse();
        }

        db.Categories.Remove(category);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.ForeignKeyViolation })
        {
            // A ticket took the category between the check and the delete: the foreign key still protects it.
            return InUse();
        }

        return Results.NoContent();
    }

    // Case-insensitive on top of the unique index (which is case-sensitive).
    private static Task<bool> NameTakenAsync(DeskDbContext db, string name, Guid? exceptId, CancellationToken ct)
    {
        var lower = name.ToLowerInvariant();
        return db.Categories.AnyAsync(c => c.Id != exceptId && c.Name.ToLower() == lower, ct);
    }

    private static bool IsUniqueViolation(DbUpdateException e) =>
        e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static IResult DuplicateName() =>
        Problems.Conflict("Une catégorie porte déjà ce nom.", ProblemTypes.DuplicateName);

    private static IResult InUse() =>
        Problems.Conflict("Cette catégorie est utilisée par des tickets.", ProblemTypes.CategoryInUse);
}
