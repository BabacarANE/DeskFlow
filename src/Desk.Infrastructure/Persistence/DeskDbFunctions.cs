namespace Desk.Infrastructure.Persistence;

/// <summary>Database functions usable in LINQ queries (translated to SQL, never executed in memory).</summary>
public static class DeskDbFunctions
{
    /// <summary>PostgreSQL <c>unaccent(text)</c> (extension created by migration, ADR 010): removes diacritics.</summary>
    public static string Unaccent(string value) =>
        throw new NotSupportedException("Unaccent is translated to SQL and cannot run in memory.");
}
