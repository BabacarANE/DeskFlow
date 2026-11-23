using Desk.Core.Domain;
using FluentValidation;

namespace Desk.Api.Contracts;

internal static class EnumNames
{
    /// <summary>Accepts the enum names only (case-insensitive), never numbers.</summary>
    public static bool TryParse<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        return value is not null
            && Enum.GetNames<T>().Contains(value, StringComparer.OrdinalIgnoreCase)
            && Enum.TryParse(value, ignoreCase: true, out result);
    }

    public static bool IsValid<T>(string? value) where T : struct, Enum => TryParse<T>(value, out _);
}

public sealed class CreateTicketRequestValidator : AbstractValidator<CreateTicketRequest>
{
    public CreateTicketRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Description).NotEmpty().MaximumLength(5000);
        RuleFor(x => x.Priority!).Must(EnumNames.IsValid<Priority>).When(x => x.Priority is not null)
            .WithMessage("Priorité invalide : Low, Normal, High ou Urgent.");
    }
}

public sealed class UpdateTicketRequestValidator : AbstractValidator<UpdateTicketRequest>
{
    public UpdateTicketRequestValidator()
    {
        RuleFor(x => x.RowVersion).NotEmpty().Must(v => RowVersions.TryParse(v, out _))
            .WithMessage("rowVersion invalide : renvoyez la valeur reçue.");
        RuleFor(x => x.Status!).Must(EnumNames.IsValid<TicketStatus>).When(x => x.Status is not null)
            .WithMessage("Statut invalide : New, Open, Pending, Resolved ou Closed.");
        RuleFor(x => x.Priority!).Must(EnumNames.IsValid<Priority>).When(x => x.Priority is not null)
            .WithMessage("Priorité invalide : Low, Normal, High ou Urgent.");
    }
}

public sealed class CreateCommentRequestValidator : AbstractValidator<CreateCommentRequest>
{
    public CreateCommentRequestValidator()
    {
        RuleFor(x => x.Body).NotEmpty().MaximumLength(5000);
    }
}

public sealed class CategoryRequestValidator : AbstractValidator<CategoryRequest>
{
    public CategoryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(60);
    }
}
