using System.Text.Json;
using Microsoft.AspNetCore.Http.Metadata;

namespace Desk.Api.Http;

/// <summary>
/// PostgreSQL refuses U+0000 in a text column (22021), which would surface as a 500. Every text input of the API
/// (query string values and every string of a JSON body, at any depth) is checked once, here, after authorization:
/// a NUL answers 422 with the offending field in <c>errors</c>. The content is never logged.
/// </summary>
public sealed class NulCharacterGuard(RequestDelegate next)
{
    /// <summary>JSON bodies of the API are small (articles are the largest); a bigger one is refused.</summary>
    public const int MaxBodyBytes = 1_048_576;

    private const string Message = "Le caractère NUL (U+0000) n'est pas autorisé.";

    public async Task InvokeAsync(HttpContext http)
    {
        if (!http.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(http);
            return;
        }

        var errors = new Dictionary<string, string[]>();
        foreach (var (key, values) in http.Request.Query)
        {
            if (key.Contains('\0'))
            {
                errors["query"] = [Message];
            }
            else if (values.Any(v => v is not null && v.Contains('\0')))
            {
                errors[key] = [Message];
            }
        }

        // A route that binds a required body (IAcceptsMetadata, set by the minimal API binder) must receive one.
        var requiresBody = http.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>() is { IsOptional: false };
        if (requiresBody && !http.Request.HasJsonContentType() && http.Request.ContentLength == 0)
        {
            await MissingBody().ExecuteAsync(http);
            return;
        }

        if (http.Request.HasJsonContentType())
        {
            if (http.Request.ContentLength > MaxBodyBytes)
            {
                await Results.Problem(statusCode: 413, title: "Corps de requête trop volumineux").ExecuteAsync(http);
                return;
            }

            byte[] body;
            try
            {
                http.Request.EnableBuffering(bufferThreshold: 32_768, bufferLimit: MaxBodyBytes);
                using var memory = new MemoryStream();
                await http.Request.Body.CopyToAsync(memory, http.RequestAborted);
                body = memory.ToArray();
                http.Request.Body.Position = 0;
            }
            catch (Exception e) when (e is InvalidDataException or IOException)
            {
                await Results.Problem(statusCode: 413, title: "Corps de requête trop volumineux").ExecuteAsync(http);
                return;
            }

            // A route that needs a body refuses an empty one, a blank one (spaces only, byte order mark only) and the literal
            // null: the binder would throw on them (500). Routes without a body keep their behaviour (empty is fine there).
            if (requiresBody && IsBlankOrNull(body))
            {
                await MissingBody().ExecuteAsync(http);
                return;
            }

            if (body.Length > 0)
            {
                // A UTF-8 byte order mark before a valid document is tolerated (System.Text.Json skips it too).
                var json = body.AsSpan(body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF ? 3 : 0);

                // Invalid UTF-8 makes the framework binder throw (500): refuse it here as the 400 it deserves.
                if (!System.Text.Unicode.Utf8.IsValid(json))
                {
                    await Results.Problem(statusCode: 400, title: "Corps de requête invalide", detail: "Le corps n'est pas de l'UTF-8 valide.").ExecuteAsync(http);
                    return;
                }

                // Malformed or too deeply nested JSON: the same 400 the binder gives, instead of a 500 from its internals.
                if (!Scan(json, out var field))
                {
                    await Results.Problem(statusCode: 400, title: "Corps de requête invalide", detail: "Le corps n'est pas un JSON valide.").ExecuteAsync(http);
                    return;
                }

                if (field is not null)
                {
                    errors[field] = [Message];
                }
            }
        }

        if (errors.Count > 0)
        {
            await Results.ValidationProblem(errors, statusCode: 422, title: "Données invalides").ExecuteAsync(http);
            return;
        }

        await next(http);
    }

    private static IResult MissingBody() =>
        Results.Problem(statusCode: 400, title: "Corps de requête invalide", detail: "Le corps de la requête est requis : envoyez un objet JSON.");

    /// <summary>True for an empty body, one made of JSON whitespace (after an optional byte order mark) and the literal null.</summary>
    public static bool IsBlankOrNull(ReadOnlySpan<byte> body)
    {
        if (body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF)
        {
            body = body[3..];
        }

        static bool IsSpace(byte b) => b is 0x20 or 0x09 or 0x0D or 0x0A;
        while (body.Length > 0 && IsSpace(body[0]))
        {
            body = body[1..];
        }

        while (body.Length > 0 && IsSpace(body[^1]))
        {
            body = body[..^1];
        }

        return body.IsEmpty || body.SequenceEqual("null"u8);
    }

    private sealed class Frame(bool isArray)
    {
        public bool IsArray { get; } = isArray;

        public int Index { get; set; } = -1;

        public string Name { get; set; } = "";
    }

    /// <summary>Path (<c>policies[0].priority</c>) of the first string, or property name, holding a NUL; null if none. Malformed JSON, invalid UTF-8 or too deep a document are left to the binder (400).</summary>
    public static string? FindNul(ReadOnlySpan<byte> json) => Scan(json, out var path) ? path : null;

    /// <summary>False when the document is not readable JSON (malformed, invalid UTF-8, too deep); otherwise the NUL path, if any.</summary>
    public static bool Scan(ReadOnlySpan<byte> json, out string? path)
    {
        path = null;
        var reader = new Utf8JsonReader(json);
        var frames = new List<Frame>();
        try
        {
            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        frames[^1].Name = reader.GetString()!;
                        if (frames[^1].Name.Contains('\0'))
                        {
                            path = PathOf(frames).Replace("\0", "", StringComparison.Ordinal);
                            return true;
                        }

                        break;
                    case JsonTokenType.StartObject or JsonTokenType.StartArray:
                        Advance(frames);
                        frames.Add(new Frame(reader.TokenType == JsonTokenType.StartArray));
                        break;
                    case JsonTokenType.EndObject or JsonTokenType.EndArray:
                        frames.RemoveAt(frames.Count - 1);
                        break;
                    case JsonTokenType.String:
                        Advance(frames);
                        if (reader.GetString()!.Contains('\0'))
                        {
                            path = PathOf(frames);
                            return true;
                        }

                        break;
                    default:
                        Advance(frames);
                        break;
                }
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException)
        {
            return false;
        }

        return true;
    }

    private static void Advance(List<Frame> frames)
    {
        if (frames.Count > 0 && frames[^1].IsArray)
        {
            frames[^1].Index++;
        }
    }

    private static string PathOf(List<Frame> frames)
    {
        var path = new System.Text.StringBuilder();
        foreach (var frame in frames)
        {
            if (frame.IsArray)
            {
                path.Append('[').Append(Math.Max(frame.Index, 0)).Append(']');
            }
            else if (frame.Name.Length > 0)
            {
                path.Append(path.Length > 0 ? "." : "").Append(frame.Name);
            }
        }

        return path.Length > 0 ? path.ToString() : "body";
    }
}
