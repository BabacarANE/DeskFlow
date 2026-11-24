using System.Text.Json;

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

            // Invalid UTF-8 makes the framework binder throw (500): refuse it here as the 400 it deserves.
            if (!System.Text.Unicode.Utf8.IsValid(body))
            {
                await Results.Problem(statusCode: 400, title: "Corps de requête invalide", detail: "Le corps n'est pas de l'UTF-8 valide.").ExecuteAsync(http);
                return;
            }

            // Malformed or too deeply nested JSON: the same 400 the binder gives, instead of a 500 from its internals.
            if (!Scan(body, out var field))
            {
                await Results.Problem(statusCode: 400, title: "Corps de requête invalide", detail: "Le corps n'est pas un JSON valide.").ExecuteAsync(http);
                return;
            }

            if (field is not null)
            {
                errors[field] = [Message];
            }
        }

        if (errors.Count > 0)
        {
            await Results.ValidationProblem(errors, statusCode: 422, title: "Données invalides").ExecuteAsync(http);
            return;
        }

        await next(http);
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
