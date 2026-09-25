using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Compositor_korean_win.Core;

/// <summary>A tool call that cannot be carried out, with the reason the caller should read.</summary>
public sealed class ToolException(string message) : Exception(message);

/// <summary>One piece of a tool's answer: text, or a PNG to look at.</summary>
public sealed record ToolContent(string Type, string? Text = null, byte[]? Data = null, string? MimeType = null)
{
    public static ToolContent Of(string text) => new("text", Text: text);
    public static ToolContent Png(byte[] data) => new("image", Data: data, MimeType: "image/png");
}

/// <summary>What a tool call returns.</summary>
public sealed class ToolResult
{
    public List<ToolContent> Content { get; } = [];
    public bool IsError { get; init; }

    public static ToolResult Text(string text)
    {
        var result = new ToolResult();
        result.Content.Add(ToolContent.Of(text));
        return result;
    }

    public static ToolResult Error(string text)
    {
        var result = new ToolResult { IsError = true };
        result.Content.Add(ToolContent.Of(text));
        return result;
    }
}

/// <summary>
/// A tool's arguments, read leniently: a number may come as a string, a colour as hex or as a list,
/// and a missing optional value is simply absent. Models write JSON loosely; refusing "12" where
/// 12 was meant helps nobody.
/// </summary>
public sealed class ToolArguments
{
    private readonly JsonElement _root;

    public ToolArguments(JsonElement root) => _root = root.ValueKind == JsonValueKind.Object ? root : default;

    public static ToolArguments Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArguments(document.RootElement.Clone());
    }

    private JsonElement? Get(string name) =>
        _root.ValueKind == JsonValueKind.Object && _root.TryGetProperty(name, out JsonElement value)
        && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? value
            : null;

    public bool Has(string name) => Get(name) is not null;

    public JsonElement? Raw(string name) => Get(name);

    public string? String(string name) => Get(name) switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString(),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        { ValueKind: JsonValueKind.True } => "true",
        { ValueKind: JsonValueKind.False } => "false",
        _ => null,
    };

    public string Required(string name) =>
        String(name) is string value && value.Length > 0 ? value : throw new ToolException($"'{name}' is required.");

    /// <summary>
    /// Words as meant: <c>\n</c> is a line break and <c>\\</c> one backslash; any other backslash
    /// stays as it is.
    /// </summary>
    /// <remarks>
    /// A model writing JSON often escapes a line break twice, and a shell cannot type one at all, so
    /// both arrive as a backslash and an n. Reading that as a line break here, for every client
    /// alike, is what lets a doubled backslash mean a real backslash and an n, which it could not
    /// if the command line and the server each unescaped in turn.
    /// </remarks>
    public string? Words(string name) => String(name) is string text ? Unescape(text) : null;

    public static string Unescape(string text)
    {
        if (!text.Contains('\\')) return text;
        var words = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length && text[i + 1] is 'n' or '\\')
            {
                words.Append(text[i + 1] == 'n' ? '\n' : '\\');
                i++;
            }
            else
            {
                words.Append(text[i]);
            }
        }
        return words.ToString();
    }

    public double? Number(string name)
    {
        JsonElement? value = Get(name);
        if (value is { ValueKind: JsonValueKind.Number } number && number.TryGetDouble(out double result)) return result;
        if (value is { ValueKind: JsonValueKind.String } text
            && double.TryParse(text.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out result)) return result;
        if (value is not null) throw new ToolException($"'{name}' should be a number.");
        return null;
    }

    public double Number(string name, double fallback) => Number(name) ?? fallback;

    public double RequiredNumber(string name) => Number(name) ?? throw new ToolException($"'{name}' is required.");

    public int? Int(string name) => Number(name) is double value ? (int)Math.Round(value) : null;

    public bool? Bool(string name) => Get(name) switch
    {
        { ValueKind: JsonValueKind.True } => true,
        { ValueKind: JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.String } text when bool.TryParse(text.GetString(), out bool parsed) => parsed,
        null => null,
        _ => throw new ToolException($"'{name}' should be true or false."),
    };

    public ToolArguments? Object(string name) =>
        Get(name) is { ValueKind: JsonValueKind.Object } value ? new ToolArguments(value) : null;

    /// <summary>A list of strings, or one string standing for a list of one.</summary>
    public List<string>? Strings(string name)
    {
        switch (Get(name))
        {
            case { ValueKind: JsonValueKind.Array } array:
                return [.. array.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.GetRawText())];
            case { ValueKind: JsonValueKind.String } single:
                return [single.GetString()!];
            case null:
                return null;
            default:
                throw new ToolException($"'{name}' should be a list of strings.");
        }
    }

    /// <summary>A point given as <c>{"x":…,"y":…}</c> or <c>[x, y]</c>.</summary>
    public Point? Position(string name)
    {
        switch (Get(name))
        {
            case { ValueKind: JsonValueKind.Object } value:
            {
                var inner = new ToolArguments(value);
                return new Point(inner.RequiredNumber("x"), inner.RequiredNumber("y"));
            }
            case { ValueKind: JsonValueKind.Array } array when array.GetArrayLength() == 2:
                return new Point(array[0].GetDouble(), array[1].GetDouble());
            case null:
                return null;
            default:
                throw new ToolException($"'{name}' should be {{\"x\": …, \"y\": …}}.");
        }
    }

    /// <summary>
    /// A colour: <c>#RGB</c>, <c>#RRGGBB</c>, <c>#RRGGBBAA</c>, <c>transparent</c>, a few names, or
    /// <c>[r, g, b]</c> / <c>[r, g, b, a]</c> in 0–255.
    /// </summary>
    public Rgba? Colour(string name)
    {
        switch (Get(name))
        {
            case { ValueKind: JsonValueKind.String } text:
                return ParseColour(text.GetString()!) ?? throw new ToolException($"'{name}' is not a colour: {text.GetString()}.");
            case { ValueKind: JsonValueKind.Array } array when array.GetArrayLength() is 3 or 4:
            {
                byte At(int i) => (byte)Math.Clamp((int)Math.Round(array[i].GetDouble()), 0, 255);
                return new Rgba(At(0), At(1), At(2), array.GetArrayLength() == 4 ? At(3) : (byte)255);
            }
            case null:
                return null;
            default:
                throw new ToolException($"'{name}' should be a colour such as \"#FF3300\".");
        }
    }

    public static Rgba? ParseColour(string text)
    {
        string value = text.Trim().ToLowerInvariant();
        switch (value)
        {
            case "transparent" or "none": return new Rgba(0, 0, 0, 0);
            case "black": return new Rgba(0, 0, 0);
            case "white": return new Rgba(255, 255, 255);
            case "red": return new Rgba(255, 0, 0);
            case "green": return new Rgba(0, 128, 0);
            case "blue": return new Rgba(0, 0, 255);
            case "yellow": return new Rgba(255, 255, 0);
            case "gray" or "grey": return new Rgba(128, 128, 128);
        }

        if (value.StartsWith('#')) value = value[1..];
        if (value.Length == 3) value = string.Concat(value.Select(c => $"{c}{c}"));
        if (value.Length is not (6 or 8) || !value.All(Uri.IsHexDigit)) return null;
        byte Part(int i) => byte.Parse(value.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return new Rgba(Part(0), Part(1), Part(2), value.Length == 8 ? Part(3) : (byte)255);
    }

    public static string Hex(Rgba colour) =>
        colour.A == 255 ? $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}" : $"#{colour.R:X2}{colour.G:X2}{colour.B:X2}{colour.A:X2}";
}

/// <summary>A JSON Schema for a tool's arguments, built from a short list of properties.</summary>
internal static class ToolSchema
{
    public sealed record Property(string Name, string Type, string Description, bool Required = false,
                                  string[]? Values = null, Property[]? Properties = null, string? Items = null);

    public static Property Int(string name, string description, bool required = false) => new(name, "integer", description, required);
    public static Property Num(string name, string description, bool required = false) => new(name, "number", description, required);
    public static Property Str(string name, string description, bool required = false) => new(name, "string", description, required);
    public static Property Bool(string name, string description) => new(name, "boolean", description);
    public static Property Colour(string name, string description, bool required = false) =>
        new(name, "string", description + " Hex like \"#FF3300\" or \"#FF330080\" with alpha, or \"transparent\".", required);
    public static Property Choice(string name, string description, string[] values, bool required = false) =>
        new(name, "string", description, required, values);
    public static Property Strings(string name, string description, bool required = false) =>
        new(name, "array", description, required, Items: "string");
    public static Property Object(string name, string description, params Property[] properties) =>
        new(name, "object", description, Properties: properties);
    public static Property PointOf(string name, string description, bool required = false) =>
        new(name, "object", description + " As {\"x\": …, \"y\": …} in document pixels.", required,
            Properties: [Num("x", "Pixels from the left edge.", true), Num("y", "Pixels from the top edge.", true)]);

    public static string Build(params Property[] properties)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteObject(writer, properties);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteObject(Utf8JsonWriter writer, Property[] properties)
    {
        writer.WriteStartObject();
        writer.WriteString("type", "object");
        writer.WriteStartObject("properties");
        foreach (Property property in properties)
        {
            writer.WritePropertyName(property.Name);
            if (property.Type == "object" && property.Properties is not null)
            {
                // Nested objects carry their own description beside their properties.
                using var stream = new MemoryStream();
                using (var inner = new Utf8JsonWriter(stream)) WriteObject(inner, property.Properties);
                using JsonDocument nested = JsonDocument.Parse(stream.ToArray());
                writer.WriteStartObject();
                foreach (JsonProperty part in nested.RootElement.EnumerateObject()) part.WriteTo(writer);
                writer.WriteString("description", property.Description);
                writer.WriteEndObject();
                continue;
            }

            writer.WriteStartObject();
            writer.WriteString("type", property.Type);
            writer.WriteString("description", property.Description);
            if (property.Values is not null)
            {
                writer.WriteStartArray("enum");
                foreach (string value in property.Values) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }
            if (property.Items is not null)
            {
                writer.WriteStartObject("items");
                writer.WriteString("type", property.Items);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();
        }
        writer.WriteEndObject();

        string[] required = [.. properties.Where(property => property.Required).Select(property => property.Name)];
        if (required.Length > 0)
        {
            writer.WriteStartArray("required");
            foreach (string name in required) writer.WriteStringValue(name);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}
