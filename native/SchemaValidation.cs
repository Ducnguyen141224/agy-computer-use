using System.Reflection;
using System.Text.Json;

namespace AgyComputerUse;

internal static class SchemaValidation
{
    private static readonly JsonDocument Schemas = Load();
    private static JsonDocument Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AgyComputerUse.ToolSchemas.json") ?? throw new InvalidOperationException("Embedded schemas are unavailable.");
        return JsonDocument.Parse(stream);
    }
    internal static void Command(string method, JsonElement value)
    {
        if (!Schemas.RootElement.TryGetProperty(method, out var schema)) throw new DesktopError("INVALID_ARGUMENT", "Unknown desktop method.");
        Validate(schema, value, "arguments");
    }
    private static void Validate(JsonElement schema, JsonElement value, string path)
    {
        bool Type(string? type) => type switch
        {
            "object" => value.ValueKind == JsonValueKind.Object,
            "array" => value.ValueKind == JsonValueKind.Array,
            "string" => value.ValueKind == JsonValueKind.String,
            "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
            "number" => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
            _ => false
        };
        var types = schema.GetProperty("type");
        if (!(types.ValueKind == JsonValueKind.Array ? types.EnumerateArray().Any(type => Type(type.GetString())) : Type(types.GetString()))) throw new DesktopError("INVALID_ARGUMENT", path + " has the wrong type.");
        if (schema.TryGetProperty("enum", out var choices) && !choices.EnumerateArray().Any(choice => choice.ValueKind == value.ValueKind && choice.GetString() == value.GetString())) throw new DesktopError("INVALID_ARGUMENT", path + " is not a supported value.");
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("maxLength", out var length) && value.GetString()!.Length > length.GetInt32()) throw new DesktopError("INVALID_ARGUMENT", path + " is too long.");
        if (value.ValueKind == JsonValueKind.String && schema.TryGetProperty("pattern", out var pattern) && !System.Text.RegularExpressions.Regex.IsMatch(value.GetString()!, pattern.GetString()!, System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw new DesktopError("INVALID_ARGUMENT", path + " has an invalid format.");
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            if (!double.IsFinite(number) || (schema.TryGetProperty("minimum", out var min) && number < min.GetDouble()) || (schema.TryGetProperty("maximum", out var max) && number > max.GetDouble())) throw new DesktopError("INVALID_ARGUMENT", path + " is out of range.");
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var count = value.GetArrayLength();
            if ((schema.TryGetProperty("minItems", out var min) && count < min.GetInt32()) || (schema.TryGetProperty("maxItems", out var max) && count > max.GetInt32())) throw new DesktopError("INVALID_ARGUMENT", path + " has an invalid item count.");
            var index = 0;
            foreach (var item in value.EnumerateArray()) Validate(schema.GetProperty("items"), item, path + "[" + index++ + "]");
        }
        if (value.ValueKind == JsonValueKind.Object)
        {
            var count = value.EnumerateObject().Count();
            if (schema.TryGetProperty("minProperties", out var minimum) && count < minimum.GetInt32() || schema.TryGetProperty("maxProperties", out var maximum) && count > maximum.GetInt32()) throw new DesktopError("INVALID_ARGUMENT", path + " has an invalid property count.");
            if (schema.TryGetProperty("required", out var required)) foreach (var key in required.EnumerateArray()) if (!value.TryGetProperty(key.GetString()!, out _)) throw new DesktopError("INVALID_ARGUMENT", path + "." + key.GetString() + " is required.");
            foreach (var item in value.EnumerateObject())
            {
                if (schema.TryGetProperty("propertyNames", out var names)) Validate(names, JsonSerializer.SerializeToElement(item.Name), path + " key");
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(item.Name, out var child)) Validate(child, item.Value, path + "." + item.Name);
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.Object) Validate(additional, item.Value, path + "." + item.Name);
                else if (additional.ValueKind != JsonValueKind.True) throw new DesktopError("INVALID_ARGUMENT", path + " contains an unknown field.");
            }
        }
    }
}
