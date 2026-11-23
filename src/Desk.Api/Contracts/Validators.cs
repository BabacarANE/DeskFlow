using Desk.Core.Domain;
using FluentValidation;

namespace Desk.Api.Contracts;

internal static class Rules
{
    public static readonly string[] InvitableRoles = [nameof(Role.TenantAdmin), nameof(Role.Agent), nameof(Role.Requester)];
    public static readonly string[] DemoRoles = ["admin", "agent", "demandeur"];
}

public sealed class CreateTenantRequestValidator : AbstractValidator<CreateTenantRequest>
{
    public CreateTenantRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(100);
        RuleFor(x => x.Slug).NotEmpty().MaximumLength(50).Matches("^[a-z0-9]+(-[a-z0-9]+)*$")
            .WithMessage("Le slug ne contient que des minuscules, chiffres et tirets.");
        RuleFor(x => x.AdminEmail).NotEmpty().MaximumLength(320).EmailAddress();
    }
}

public sealed class UpdateTenantRequestValidator : AbstractValidator<UpdateTenantRequest>
{
    public UpdateTenantRequestValidator()
    {
        RuleFor(x => x).Must(x => x.Name is not null || x.IsActive is not null)
            .WithName("body").WithMessage("Au moins un champ à modifier est requis.");
        RuleFor(x => x.Name!).NotEmpty().MaximumLength(100).When(x => x.Name is not null);
    }
}

public sealed class UpdateUserRequestValidator : AbstractValidator<UpdateUserRequest>
{
    public UpdateUserRequestValidator()
    {
        RuleFor(x => x).Must(x => x.Role is not null || x.IsActive is not null)
            .WithName("body").WithMessage("Au moins un champ à modifier est requis.");
        RuleFor(x => x.Role!).Must(r => Rules.InvitableRoles.Contains(r))
            .When(x => x.Role is not null)
            .WithMessage("Rôle invalide : TenantAdmin, Agent ou Requester.");
    }
}

public sealed class CreateInvitationRequestValidator : AbstractValidator<CreateInvitationRequest>
{
    public CreateInvitationRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().MaximumLength(320).EmailAddress();
        RuleFor(x => x.Role).NotEmpty().Must(r => r is not null && Rules.InvitableRoles.Contains(r))
            .WithMessage("Rôle invalide : TenantAdmin, Agent ou Requester.");
    }
}

public sealed class DemoLoginRequestValidator : AbstractValidator<DemoLoginRequest>
{
    public DemoLoginRequestValidator()
    {
        RuleFor(x => x.Role).NotEmpty().Must(r => r is not null && Rules.DemoRoles.Contains(r))
            .WithMessage("Rôle de démo invalide : admin, agent ou demandeur.");
    }
}
