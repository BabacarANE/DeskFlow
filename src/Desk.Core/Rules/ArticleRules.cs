namespace Desk.Core.Rules;

/// <summary>Tag storage and excerpt rules of the knowledge base (docs/api/phase4.md section 1).</summary>
public static class ArticleRules
{
    public const int MaxTags = 10;
    public const int MaxTagLength = 30;
    public const int ExcerptLength = 200;

    /// <summary>Trims, drops duplicates (case-insensitive, first spelling wins) and keeps the order.</summary>
    public static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (var tag in tags.Select(t => t.Trim()))
        {
            if (seen.Add(tag))
            {
                result.Add(tag);
            }
        }

        return result;
    }

    /// <summary>The tag is storable: 1 to 30 characters once trimmed, no comma.</summary>
    public static bool IsValidTag(string? tag) =>
        tag is not null && tag.Trim() is { Length: > 0 and <= MaxTagLength } trimmed && !trimmed.Contains(',');

    public static string JoinTags(IEnumerable<string> tags) => string.Join(',', tags);

    public static IReadOnlyList<string> SplitTags(string stored) =>
        stored.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Start of the symptom, 200 characters at most, never cutting a surrogate pair.</summary>
    public static string Excerpt(string symptom)
    {
        if (symptom.Length <= ExcerptLength)
        {
            return symptom;
        }

        var length = char.IsHighSurrogate(symptom[ExcerptLength - 1]) ? ExcerptLength - 1 : ExcerptLength;
        return symptom[..length];
    }

    /// <summary>Words of a search text: split on whitespace, words shorter than 2 characters ignored, duplicates removed.</summary>
    public static List<string> SearchWords(string? q) =>
        (q ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>Escapes the LIKE wildcards and the escape character itself so that a word is matched as plain text.</summary>
    public static string EscapeLike(string word) =>
        word.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
}
