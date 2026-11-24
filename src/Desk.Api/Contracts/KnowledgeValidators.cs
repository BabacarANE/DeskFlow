using Desk.Core.Domain;
using Desk.Core.Rules;
using FluentValidation;

namespace Desk.Api.Contracts;

public sealed class ArticleRequestValidator : AbstractValidator<ArticleRequest>
{
    public const int MaxSymptom = 4000;
    public const int MaxCause = 4000;
    public const int MaxSolution = 8000;

    public ArticleRequestValidator()
    {
        RuleFor(x => x.Title).NotNull().WithMessage("Le titre est requis.")
            .Must(t => t is null || (t.Trim().Length is >= 3 and <= 200)).WithMessage("Le titre doit contenir de 3 à 200 caractères.");
        RuleFor(x => x.Symptom).NotNull().WithMessage("Le symptôme est requis (il peut être vide pour un brouillon).")
            .MaximumLength(MaxSymptom);
        RuleFor(x => x.Cause).NotNull().WithMessage("La cause est requise (elle peut être vide).")
            .MaximumLength(MaxCause);
        RuleFor(x => x.Solution).NotNull().WithMessage("La solution est requise (elle peut être vide pour un brouillon).")
            .MaximumLength(MaxSolution);
        RuleFor(x => x.Tags).NotNull().WithMessage("Les étiquettes sont requises (tableau, éventuellement vide).");
        RuleFor(x => x.Tags!).Must(t => t.Count <= ArticleRules.MaxTags)
            .When(x => x.Tags is not null)
            .WithMessage($"Au plus {ArticleRules.MaxTags} étiquettes.");
        RuleFor(x => x.Tags!).Must(t => t.All(ArticleRules.IsValidTag))
            .When(x => x.Tags is not null)
            .WithMessage($"Chaque étiquette contient de 1 à {ArticleRules.MaxTagLength} caractères, sans virgule.");
    }
}

public sealed class ThemeRequestValidator : AbstractValidator<ThemeRequest>
{
    private const string Message = "Couleur invalide : format #RRGGBB attendu.";

    public ThemeRequestValidator()
    {
        RuleFor(x => x.ColorPrimary).Must(ColorContrast.IsHex).WithMessage(Message);
        RuleFor(x => x.ColorSurface).Must(ColorContrast.IsHex).WithMessage(Message);
        RuleFor(x => x.ColorAccent).Must(ColorContrast.IsHex).WithMessage(Message);
    }
}

public sealed class UpdateSlaPoliciesRequestValidator : AbstractValidator<UpdateSlaPoliciesRequest>
{
    public const int MaxMinutes = 43_200;

    public UpdateSlaPoliciesRequestValidator()
    {
        RuleFor(x => x).Custom((request, context) =>
        {
            if (request.Policies is not { Count: 4 } policies)
            {
                context.AddFailure("policies", "Exactement quatre lignes sont requises : Low, Normal, High et Urgent.");
                return;
            }

            var seen = new HashSet<Priority>();
            for (var i = 0; i < policies.Count; i++)
            {
                var key = $"policies[{i}]";
                if (policies[i] is not { } row)
                {
                    context.AddFailure(key, "Ligne manquante.");
                    continue;
                }

                if (!EnumNames.TryParse<Priority>(row.Priority, out var priority))
                {
                    context.AddFailure($"{key}.priority", "Priorité invalide : Low, Normal, High ou Urgent.");
                }
                else if (!seen.Add(priority))
                {
                    context.AddFailure($"{key}.priority", "Une seule ligne par priorité.");
                }

                var first = InRange(context, $"{key}.firstResponseMinutes", row.FirstResponseMinutes);
                var resolution = InRange(context, $"{key}.resolutionMinutes", row.ResolutionMinutes);
                if (first is { } f && resolution is { } r && r < f)
                {
                    context.AddFailure($"{key}.resolutionMinutes", "Le délai de résolution doit être supérieur ou égal au délai de première réponse.");
                }
            }
        });
    }

    private static int? InRange(ValidationContext<UpdateSlaPoliciesRequest> context, string key, int? value)
    {
        if (value is >= 1 and <= MaxMinutes)
        {
            return value;
        }

        context.AddFailure(key, $"Une durée en minutes de 1 à {MaxMinutes} est requise.");
        return null;
    }
}
