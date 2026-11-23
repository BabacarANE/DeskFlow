using Desk.Core.Domain;

namespace Desk.Api.Contracts;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);

public sealed record AccessTokenResponse(string AccessToken, int ExpiresIn);

public sealed record DemoLoginRequest(string? Role);

public sealed record ThemeDto(string ColorPrimary, string ColorSurface, string ColorAccent);

public sealed record MeTenantDto(Guid Id, string Name, string Slug, ThemeDto Theme);

public sealed record MeDto(Guid Id, string Email, string DisplayName, string? AvatarUrl, Role Role, MeTenantDto? Tenant, bool IsDemo);

public sealed record TenantDto(Guid Id, string Name, string Slug, bool IsActive, DateTimeOffset CreatedAt)
{
    public static TenantDto From(Tenant t) => new(t.Id, t.Name, t.Slug, t.IsActive, t.CreatedAt);
}

public sealed record CreateTenantRequest(string? Name, string? Slug, string? AdminEmail);

public sealed record UpdateTenantRequest(string? Name, bool? IsActive);

public sealed record UserDto(Guid Id, string Email, string DisplayName, string? AvatarUrl, Role Role, bool IsActive, DateTimeOffset CreatedAt)
{
    public static UserDto From(User u) => new(u.Id, u.Email, u.DisplayName, u.AvatarUrl, u.Role, u.IsActive, u.CreatedAt);
}

public sealed record UpdateUserRequest(string? Role, bool? IsActive);

public sealed record InvitationDto(Guid Id, string Email, Role Role, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, InvitationStatus Status);

public sealed record CreateInvitationRequest(string? Email, string? Role);
