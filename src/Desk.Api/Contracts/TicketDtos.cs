using System.Text.Json;
using System.Text.Json.Serialization;
using Desk.Core.Domain;

namespace Desk.Api.Contracts;

public sealed record UserRefDto(Guid Id, string DisplayName)
{
    public static UserRefDto? From(User? u) => u is null ? null : new(u.Id, u.DisplayName);
}

public sealed record CategoryDto(Guid Id, string Name)
{
    public static CategoryDto? From(Category? c) => c is null ? null : new(c.Id, c.Name);
}

public sealed record CategoryRequest(string? Name);

public sealed record CommentDto(Guid Id, UserRefDto Author, string Body, bool IsInternal, DateTimeOffset CreatedAt)
{
    public static CommentDto From(TicketComment c) => new(c.Id, new UserRefDto(c.AuthorId, c.Author?.DisplayName ?? ""), c.Body, c.IsInternal, c.CreatedAt);
}

public record TicketSummaryDto(
    Guid Id,
    int Number,
    string Title,
    TicketStatus Status,
    Priority Priority,
    CategoryDto? Category,
    UserRefDto Requester,
    UserRefDto? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    DateTimeOffset? EscalatedAt)
{
    public static TicketSummaryDto From(Ticket t) => new(
        t.Id, t.Number, t.Title, t.Status, t.Priority, CategoryDto.From(t.Category),
        UserRefDto.From(t.Requester)!, UserRefDto.From(t.Assignee), t.CreatedAt, t.UpdatedAt,
        t.ResponseDueAt, t.ResolutionDueAt, t.EscalatedAt);
}

/// <summary>Serializes as the flat TicketDetail of the contract (TicketSummary plus the fields below).</summary>
public sealed record TicketDetailDto(
    Guid Id,
    int Number,
    string Title,
    TicketStatus Status,
    Priority Priority,
    CategoryDto? Category,
    UserRefDto Requester,
    UserRefDto? Assignee,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ResponseDueAt,
    DateTimeOffset? ResolutionDueAt,
    DateTimeOffset? EscalatedAt,
    string Description,
    string RowVersion,
    DateTimeOffset? FirstResponseAt,
    DateTimeOffset? ResolvedAt,
    IReadOnlyList<CommentDto> Comments)
{
    public static TicketDetailDto From(Ticket t, IReadOnlyList<CommentDto> comments) => new(
        t.Id, t.Number, t.Title, t.Status, t.Priority, CategoryDto.From(t.Category),
        UserRefDto.From(t.Requester)!, UserRefDto.From(t.Assignee), t.CreatedAt, t.UpdatedAt,
        t.ResponseDueAt, t.ResolutionDueAt, t.EscalatedAt,
        t.Description, RowVersions.Format(t.RowVersion), t.FirstResponseAt, t.ResolvedAt, comments);
}

public sealed record TicketEventDto(Guid Id, TicketEventType Type, UserRefDto? Actor, string? OldValue, string? NewValue, DateTimeOffset At)
{
    public static TicketEventDto From(TicketEvent e) =>
        new(e.Id, e.Type, e.ActorId is { } id ? new UserRefDto(id, e.Actor?.DisplayName ?? "") : null, e.OldValue, e.NewValue, e.At);
}

public sealed record CreateTicketRequest(string? Title, string? Description, Guid? CategoryId, string? Priority);

public sealed record UpdateTicketRequest(string? RowVersion, string? Status, string? Priority, OptionalGuid AssigneeId, OptionalGuid CategoryId);

public sealed record CreateCommentRequest(string? Body, bool? IsInternal);

/// <summary>The opaque token is the PostgreSQL xmin of the row, as text.</summary>
public static class RowVersions
{
    public static string Format(uint version) => version.ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static bool TryParse(string? token, out uint version) =>
        uint.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version);
}

/// <summary>A PATCH property that tells "absent" (unchanged) from <c>null</c> (clear) from a value.</summary>
[JsonConverter(typeof(OptionalGuidConverter))]
/// <param name="Malformed">The value was present but is not a UUID: reported as a 422 by the validator.</param>
public readonly record struct OptionalGuid(bool IsSet, Guid? Value, bool Malformed = false);

public sealed class OptionalGuidConverter : JsonConverter<OptionalGuid>
{
    public override bool HandleNull => true;

    public override OptionalGuid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return new OptionalGuid(true, null);
        }

        if (reader.TokenType == JsonTokenType.String && reader.TryGetGuid(out var id))
        {
            return new OptionalGuid(true, id);
        }

        using var _ = JsonDocument.ParseValue(ref reader); // consumes the whole value, objects and arrays included
        return new OptionalGuid(true, null, Malformed: true);
    }

    public override void Write(Utf8JsonWriter writer, OptionalGuid value, JsonSerializerOptions options)
    {
        if (value.Value is { } id)
        {
            writer.WriteStringValue(id);
        }
        else
        {
            writer.WriteNullValue();
        }
    }
}
