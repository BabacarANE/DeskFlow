using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Desk.Core.Events;

/// <summary>JSON conventions of every message: camelCase, enums as strings, UTC timestamps with 6 decimals.</summary>
public static class EventJson
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        options.Converters.Add(new UtcTimestampConverter());
        return options;
    }

    /// <summary>"2026-11-23T10:00:00.000000Z": fixed format, always UTC.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        public const string Format = "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}

/// <summary>The JSON body of every message of the <c>desk.events</c> exchange (docs/api/events.md, section 1).</summary>
public sealed record MessageEnvelope(
    Guid MessageId, string Type, Guid TenantId, DateTimeOffset OccurredAt, string CorrelationId, JsonElement Payload)
{
    public static MessageEnvelope Create<TPayload>(
        Guid messageId, string type, Guid tenantId, DateTimeOffset occurredAt, string correlationId, TPayload payload) =>
        new(messageId, type, tenantId, occurredAt, correlationId,
            JsonSerializer.SerializeToElement(payload, EventJson.Options));

    public byte[] ToUtf8Json() => JsonSerializer.SerializeToUtf8Bytes(this, EventJson.Options);

    public string ToJson() => JsonSerializer.Serialize(this, EventJson.Options);

    public TPayload GetPayload<TPayload>() =>
        Payload.Deserialize<TPayload>(EventJson.Options) ?? throw new JsonException("Payload is null.");

    /// <summary>
    /// Validates a raw message. False means the message is poisoned (never worth a retry):
    /// invalid JSON, missing or empty identifiers, missing tenant, payload that is not an object.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> body, [NotNullWhen(true)] out MessageEnvelope? envelope, out string reason)
    {
        envelope = null;
        Raw? raw;
        try
        {
            raw = JsonSerializer.Deserialize<Raw>(body, EventJson.Options);
        }
        catch (JsonException ex)
        {
            reason = "Invalid JSON: " + ex.Message;
            return false;
        }

        if (raw is null)
        {
            reason = "Empty message.";
            return false;
        }

        if (raw.MessageId is null || raw.MessageId == Guid.Empty)
        {
            reason = "Missing messageId.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw.Type))
        {
            reason = "Missing type.";
            return false;
        }

        if (raw.TenantId is null || raw.TenantId == Guid.Empty)
        {
            reason = "Missing tenantId.";
            return false;
        }

        if (raw.OccurredAt is null)
        {
            reason = "Missing occurredAt.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(raw.CorrelationId))
        {
            reason = "Missing correlationId.";
            return false;
        }

        if (raw.Payload is not { ValueKind: JsonValueKind.Object } payload)
        {
            reason = "Payload is missing or is not an object.";
            return false;
        }

        envelope = new MessageEnvelope(raw.MessageId.Value, raw.Type, raw.TenantId.Value, raw.OccurredAt.Value, raw.CorrelationId, payload);
        reason = "";
        return true;
    }

    // Nullable mirror of the envelope: lets the parser tell "absent" from "empty".
    private sealed record Raw(Guid? MessageId, string? Type, Guid? TenantId, DateTimeOffset? OccurredAt, string? CorrelationId, JsonElement? Payload);
}
