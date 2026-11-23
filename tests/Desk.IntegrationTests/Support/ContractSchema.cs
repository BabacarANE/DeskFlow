using System.Text.Json;
using System.Text.RegularExpressions;

namespace Desk.IntegrationTests.Support;

/// <summary>
/// Minimal OpenAPI 3.0 schema checker for the frozen contract: $ref, allOf, type, nullable, enum, required,
/// properties, items, format uuid/date-time. Unknown properties are tolerated (the contract is not closed).
/// </summary>
public sealed class ContractSchema(JsonElement document)
{
    public static ContractSchema Load()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "api", "openapi.json");
            if (File.Exists(candidate))
            {
                return new ContractSchema(JsonDocument.Parse(File.ReadAllText(candidate)).RootElement.Clone());
            }
        }

        throw new FileNotFoundException("docs/api/openapi.json not found");
    }

    /// <summary>The schema of the JSON response declared for an operation and status code.</summary>
    public JsonElement ResponseSchema(string path, string method, int status)
    {
        var response = document.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses").GetProperty(status.ToString());
        if (response.TryGetProperty("$ref", out var reference))
        {
            response = Resolve(reference.GetString()!);
        }

        foreach (var content in response.GetProperty("content").EnumerateObject())
        {
            return content.Value.GetProperty("schema");
        }

        throw new InvalidOperationException("No JSON content");
    }

    public JsonElement Component(string name) => document.GetProperty("components").GetProperty("schemas").GetProperty(name);

    public List<string> Validate(JsonElement value, JsonElement schema, string at = "$")
    {
        var errors = new List<string>();
        Check(value, schema, at, errors);
        return errors;
    }

    private JsonElement Resolve(string reference)
    {
        var element = document;
        foreach (var part in reference.TrimStart('#', '/').Split('/'))
        {
            element = element.GetProperty(part);
        }

        return element;
    }

    private void Check(JsonElement value, JsonElement schema, string at, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            Check(value, Resolve(reference.GetString()!), at, errors);
            return;
        }

        if (schema.TryGetProperty("allOf", out var allOf))
        {
            foreach (var part in allOf.EnumerateArray())
            {
                // nullable lives next to allOf in the contract: handled below before recursing on null.
                if (value.ValueKind == JsonValueKind.Null && IsNullable(schema))
                {
                    return;
                }

                Check(value, part, at, errors);
            }

            return;
        }

        if (value.ValueKind == JsonValueKind.Null)
        {
            if (!IsNullable(schema))
            {
                errors.Add($"{at}: null is not allowed");
            }

            return;
        }

        if (schema.TryGetProperty("enum", out var allowed) && !allowed.EnumerateArray().Any(e => e.ToString() == value.ToString()))
        {
            errors.Add($"{at}: '{value}' is not in the enum");
        }

        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "object":
                CheckObject(value, schema, at, errors);
                break;
            case "array":
                if (value.ValueKind != JsonValueKind.Array)
                {
                    errors.Add($"{at}: array expected");
                    break;
                }

                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Check(item, schema.GetProperty("items"), $"{at}[{index++}]", errors);
                }

                break;
            case "string":
                CheckString(value, schema, at, errors);
                break;
            case "integer":
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out _))
                {
                    errors.Add($"{at}: integer expected");
                }

                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    errors.Add($"{at}: boolean expected");
                }

                break;
        }
    }

    private static bool IsNullable(JsonElement schema) => schema.TryGetProperty("nullable", out var n) && n.GetBoolean();

    private void CheckObject(JsonElement value, JsonElement schema, string at, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{at}: object expected");
            return;
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(r => r.GetString()!))
            {
                if (!value.TryGetProperty(name, out _))
                {
                    errors.Add($"{at}.{name}: required property is missing");
                }
            }
        }

        if (schema.TryGetProperty("properties", out var properties))
        {
            foreach (var property in properties.EnumerateObject())
            {
                if (value.TryGetProperty(property.Name, out var inner))
                {
                    Check(inner, property.Value, $"{at}.{property.Name}", errors);
                }
            }
        }
    }

    private static void CheckString(JsonElement value, JsonElement schema, string at, List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{at}: string expected");
            return;
        }

        var text = value.GetString()!;
        var format = schema.TryGetProperty("format", out var f) ? f.GetString() : null;
        if (format == "uuid" && !Guid.TryParse(text, out _))
        {
            errors.Add($"{at}: uuid expected");
        }

        if (format == "date-time" && !DateTimeOffset.TryParse(text, out _))
        {
            errors.Add($"{at}: date-time expected");
        }

        if (schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32())
        {
            errors.Add($"{at}: longer than {max}");
        }

        if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!))
        {
            errors.Add($"{at}: does not match the pattern");
        }
    }
}
