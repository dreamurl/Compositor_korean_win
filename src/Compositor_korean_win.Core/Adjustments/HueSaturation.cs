using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Compositor_korean_win.Core;

/// <summary>The six colour ranges plus Master, as in Photoshop's Hue/Saturation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ColorRange>))]
public enum ColorRange
{
    [JsonStringEnumMemberName("Master")] Master,
    [JsonStringEnumMemberName("Reds")] Reds,
    [JsonStringEnumMemberName("Yellows")] Yellows,
    [JsonStringEnumMemberName("Greens")] Greens,
    [JsonStringEnumMemberName("Cyans")] Cyans,
    [JsonStringEnumMemberName("Blues")] Blues,
    [JsonStringEnumMemberName("Magentas")] Magentas,
}

public static class ColorRanges
{
    public static readonly ColorRange[] All =
    [
        ColorRange.Master, ColorRange.Reds, ColorRange.Yellows, ColorRange.Greens,
        ColorRange.Cyans, ColorRange.Blues, ColorRange.Magentas,
    ];

    /// <summary>Everything but Master — the ranges that claim only part of the spectrum.</summary>
    public static readonly ColorRange[] Colors = All[1..];

    /// <summary>Photoshop's starting band: falloff start, range start, range end, falloff end.</summary>
    public static HueBand DefaultBand(this ColorRange range) => range switch
    {
        ColorRange.Master => new HueBand(0, 0, 360, 360),
        ColorRange.Reds => new HueBand(315, 345, 15, 45),
        ColorRange.Yellows => new HueBand(15, 45, 75, 105),
        ColorRange.Greens => new HueBand(75, 105, 135, 165),
        ColorRange.Cyans => new HueBand(135, 165, 195, 225),
        ColorRange.Blues => new HueBand(195, 225, 255, 285),
        ColorRange.Magentas => new HueBand(255, 285, 315, 345),
        _ => new HueBand(0, 0, 360, 360),
    };
}

/// <summary>
/// A hue band in degrees, wrapping at 360: full strength between the range bounds, fading to
/// nothing at the falloff bounds.
/// </summary>
public sealed record HueBand
{
    [JsonPropertyName("falloffStart")] public double FalloffStart { get; init; }
    [JsonPropertyName("rangeStart")] public double RangeStart { get; init; }
    [JsonPropertyName("rangeEnd")] public double RangeEnd { get; init; }
    [JsonPropertyName("falloffEnd")] public double FalloffEnd { get; init; }

    public HueBand() { }

    public HueBand(double falloffStart, double rangeStart, double rangeEnd, double falloffEnd)
    {
        FalloffStart = falloffStart;
        RangeStart = rangeStart;
        RangeEnd = rangeEnd;
        FalloffEnd = falloffEnd;
    }

    /// <summary>Degrees from one hue forward to another, always 0–360.</summary>
    public static double Forward(double from, double to)
    {
        double delta = (to - from) % 360;
        return delta < 0 ? delta + 360 : delta;
    }

    /// <summary>
    /// How strongly this band claims a hue: 1 inside the range, ramping linearly through each
    /// falloff shoulder, 0 outside. Measuring forward is what handles the wrap at 360.
    /// </summary>
    public double Weight(double hue)
    {
        double span = Forward(FalloffStart, FalloffEnd);
        if (span <= 0) return 1; // Master covers everything.

        double position = Forward(FalloffStart, hue);
        if (position > span) return 0;

        double rampIn = Forward(FalloffStart, RangeStart);
        double plateauEnd = Forward(FalloffStart, RangeEnd);
        if (position < rampIn) return rampIn > 0 ? position / rampIn : 1;
        if (position <= plateauEnd) return 1;

        double rampOut = span - plateauEnd;
        return rampOut > 0 ? (span - position) / rampOut : 1;
    }

    [JsonIgnore]
    public double[] Handles => [FalloffStart, RangeStart, RangeEnd, FalloffEnd];

    /// <summary>The same band centred on one hue, keeping its core and shoulder widths.</summary>
    public HueBand CenteredOn(double hue)
    {
        double core = Forward(RangeStart, RangeEnd);
        double leading = Forward(FalloffStart, RangeStart);
        double trailing = Forward(RangeEnd, FalloffEnd);

        double rangeStart = Wrap(hue - core / 2);
        double rangeEnd = Wrap(rangeStart + core);
        return new HueBand(Wrap(rangeStart - leading), rangeStart, rangeEnd, Wrap(rangeEnd + trailing));

        static double Wrap(double value) => ((value % 360) + 360) % 360;
    }
}

/// <summary>One colour range's three sliders.</summary>
public sealed record RangeAdjustment
{
    [JsonPropertyName("hue")] public double Hue { get; init; }
    [JsonPropertyName("saturation")] public double Saturation { get; init; }
    [JsonPropertyName("lightness")] public double Lightness { get; init; }

    public RangeAdjustment() { }

    public RangeAdjustment(double hue, double saturation, double lightness)
    {
        Hue = hue;
        Saturation = saturation;
        Lightness = lightness;
    }
}

/// <summary>
/// Values held per colour range, comparing by contents and serialising the way Swift does.
/// </summary>
/// <remarks>
/// Swift's <c>JSONEncoder</c> writes a dictionary as a JSON object only when its key is
/// <c>String</c> or <c>Int</c>. <c>ColorRange</c> is neither — it is an enum with a string raw
/// value — so upstream's <c>[ColorRange: RangeAdjustment]</c> lands on disk as a flat array of
/// alternating keys and values, not as an object. Reproducing that is what lets a project written
/// on macOS open here.
///
/// Swift's dictionary order is unspecified, so the pairs arrive in no particular order and this
/// must not depend on one. Storage is a slot per range, which also makes equality order-free.
/// </remarks>
public sealed class ColorRangeMap<TValue> : IReadOnlyDictionary<ColorRange, TValue>,
                                            IEquatable<ColorRangeMap<TValue>>
    where TValue : class
{
    private readonly TValue?[] _slots;

    public ColorRangeMap() => _slots = new TValue?[ColorRanges.All.Length];

    public ColorRangeMap(IEnumerable<KeyValuePair<ColorRange, TValue>> entries) : this()
    {
        foreach ((ColorRange range, TValue value) in entries) _slots[(int)range] = value;
    }

    public static ColorRangeMap<TValue> Empty { get; } = new();

    public TValue this[ColorRange key] =>
        _slots[(int)key] ?? throw new KeyNotFoundException(key.ToString());

    public IEnumerable<ColorRange> Keys => ColorRanges.All.Where(range => _slots[(int)range] is not null);
    public IEnumerable<TValue> Values => Keys.Select(range => _slots[(int)range]!);
    public int Count => _slots.Count(slot => slot is not null);

    public bool ContainsKey(ColorRange key) => _slots[(int)key] is not null;

    public bool TryGetValue(ColorRange key, out TValue value)
    {
        value = _slots[(int)key]!;
        return value is not null;
    }

    /// <summary>The value for a range, or <paramref name="fallback"/> when it has none.</summary>
    public TValue ValueOr(ColorRange key, TValue fallback) => _slots[(int)key] ?? fallback;

    /// <summary>The same map with one range set.</summary>
    public ColorRangeMap<TValue> With(ColorRange key, TValue value)
    {
        var copy = new ColorRangeMap<TValue>();
        Array.Copy(_slots, copy._slots, _slots.Length);
        copy._slots[(int)key] = value;
        return copy;
    }

    public IEnumerator<KeyValuePair<ColorRange, TValue>> GetEnumerator() =>
        Keys.Select(range => new KeyValuePair<ColorRange, TValue>(range, _slots[(int)range]!)).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool Equals(ColorRangeMap<TValue>? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;

        var comparer = EqualityComparer<TValue?>.Default;
        for (int i = 0; i < _slots.Length; i++)
            if (!comparer.Equals(_slots[i], other._slots[i])) return false;

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as ColorRangeMap<TValue>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (TValue? slot in _slots) hash.Add(slot);
        return hash.ToHashCode();
    }
}

/// <summary>Reads and writes a <see cref="ColorRangeMap{TValue}"/> as Swift's flat key/value array.</summary>
public sealed class ColorRangeMapConverter<TValue> : JsonConverter<ColorRangeMap<TValue>>
    where TValue : class
{
    public override ColorRangeMap<TValue> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an array");

        var keys = (JsonTypeInfo<ColorRange>)options.GetTypeInfo(typeof(ColorRange));
        var values = (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));

        var entries = new List<KeyValuePair<ColorRange, TValue>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            ColorRange key = JsonSerializer.Deserialize(ref reader, keys);
            if (!reader.Read()) throw new JsonException("a key with no value");
            TValue value = JsonSerializer.Deserialize(ref reader, values)
                           ?? throw new JsonException("a null value");
            entries.Add(new KeyValuePair<ColorRange, TValue>(key, value));
        }

        return new ColorRangeMap<TValue>(entries);
    }

    public override void Write(Utf8JsonWriter writer, ColorRangeMap<TValue> value, JsonSerializerOptions options)
    {
        var keys = (JsonTypeInfo<ColorRange>)options.GetTypeInfo(typeof(ColorRange));
        var values = (JsonTypeInfo<TValue>)options.GetTypeInfo(typeof(TValue));

        writer.WriteStartArray();
        foreach ((ColorRange key, TValue item) in value)
        {
            JsonSerializer.Serialize(writer, key, keys);
            JsonSerializer.Serialize(writer, item, values);
        }
        writer.WriteEndArray();
    }
}

/// <summary>
/// Hue/Saturation: hue −180…180 (0…360 colorizing), saturation −100…100 (0…100 colorizing) and
/// lightness −100…100, kept per colour range. Master applies everywhere.
/// </summary>
public sealed record HueSaturationSettings
{
    /// <summary>Which range the sliders and the spectrum edit.</summary>
    [JsonPropertyName("range")] public ColorRange Range { get; init; } = ColorRange.Master;

    [JsonPropertyName("colorize")] public bool Colorize { get; init; }

    /// <summary>Applies the selected range to everything outside its band instead.</summary>
    [JsonPropertyName("invertRange")] public bool InvertRange { get; init; }

    [JsonPropertyName("adjustments")]
    [JsonConverter(typeof(ColorRangeMapConverter<RangeAdjustment>))]
    public ColorRangeMap<RangeAdjustment> Adjustments { get; init; } = ColorRangeMap<RangeAdjustment>.Empty;

    [JsonPropertyName("bands")]
    [JsonConverter(typeof(ColorRangeMapConverter<HueBand>))]
    public ColorRangeMap<HueBand> Bands { get; init; } = DefaultBands;

    public static ColorRangeMap<HueBand> DefaultBands { get; } = new(
        ColorRanges.All.Select(range => new KeyValuePair<ColorRange, HueBand>(range, range.DefaultBand())));

    /// <summary>The settings a plain hue/saturation/lightness triple stands for.</summary>
    public static HueSaturationSettings From(double hue, double saturation, double lightness,
                                             bool colorize, ColorRange range = ColorRange.Master) =>
        new()
        {
            Range = range,
            Colorize = colorize,
            Adjustments = ColorRangeMap<RangeAdjustment>.Empty
                .With(range, new RangeAdjustment(hue, saturation, lightness)),
        };

    /// <summary>Photoshop's starting point when Colorize is switched on.</summary>
    public static HueSaturationSettings ColorizeStart { get; } = From(0, 25, 0, colorize: true);

    [JsonIgnore]
    public RangeAdjustment Current => Adjustments.ValueOr(Range, new RangeAdjustment());

    [JsonIgnore]
    public double Hue => Current.Hue;

    [JsonIgnore]
    public double Saturation => Current.Saturation;

    [JsonIgnore]
    public double Lightness => Current.Lightness;

    [JsonIgnore]
    public HueBand Band => Bands.ValueOr(Range, Range.DefaultBand());
}
