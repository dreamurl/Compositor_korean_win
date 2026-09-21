using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Compositor_korean_win.Core;

/// <summary>One handle on a Curves curve, in 0–255 input and output.</summary>
public readonly record struct CurvePoint(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y);

/// <summary>A Curves adjustment: one curve per channel, composite first.</summary>
public sealed record CurvesSettings
{
    private static EquatableList<CurvePoint> Diagonal =>
        new([new CurvePoint(0, 0), new CurvePoint(255, 255)]);

    [JsonPropertyName("channel")] public LevelsChannel Channel { get; init; } = LevelsChannel.Rgb;

    [JsonPropertyName("channels")]
    [JsonConverter(typeof(CurveChannelsConverter))]
    public EquatableList<EquatableList<CurvePoint>> Channels { get; init; } =
        new([Diagonal, Diagonal, Diagonal, Diagonal]);

    /// <summary>
    /// What upstream's <c>ProjectStore</c> requires of a stored curve: four channels, two to
    /// thirty-two handles each, spanning the full 0–255 input and strictly increasing across it.
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        Channels.Count == 4 && Channels.All(points =>
            points.Count is >= 2 and <= 32
            && points[0].X == 0 && points[^1].X == 255
            && points.All(point => double.IsFinite(point.X) && double.IsFinite(point.Y)
                                   && point.X is >= 0 and <= 255 && point.Y is >= 0 and <= 255)
            && Enumerable.Range(0, points.Count - 1).All(i => points[i].X < points[i + 1].X));

    /// <summary>
    /// The curve's output for <paramref name="x"/>, by shape-preserving cubic Hermite
    /// interpolation, which does not overshoot between handles the way a natural spline would.
    /// </summary>
    public double Value(double x, int channel)
    {
        EquatableList<CurvePoint> p = Channels[channel];

        int i = 0;
        for (int candidate = 0; candidate < p.Count; candidate++)
            if (p[candidate].X <= x) i = candidate;
        i = Math.Min(p.Count - 2, Math.Max(0, i));

        double[] d = new double[p.Count - 1];
        for (int j = 0; j < d.Length; j++)
            d[j] = (p[j + 1].Y - p[j].Y) / (p[j + 1].X - p[j].X);

        double Slope(int j)
        {
            if (j == 0) return d[0];
            if (j == p.Count - 1) return d[^1];
            if (d[j - 1] * d[j] <= 0) return 0;
            return 2 / (1 / d[j - 1] + 1 / d[j]);
        }

        double h = p[i + 1].X - p[i].X;
        double t = Math.Min(1, Math.Max(0, (x - p[i].X) / h));
        double y = (2 * t * t * t - 3 * t * t + 1) * p[i].Y
                 + (t * t * t - 2 * t * t + t) * h * Slope(i)
                 + (-2 * t * t * t + 3 * t * t) * p[i + 1].Y
                 + (t * t * t - t * t) * h * Slope(i + 1);
        return Math.Min(255, Math.Max(0, y));
    }

    /// <summary>
    /// The 3 × 256 table the <c>levels_apply</c> kernel takes: each channel's curve followed by
    /// the composite one, as fractions of full scale.
    /// </summary>
    public float[] Table()
    {
        float[] table = new float[3 * 256];
        for (int channel = 1; channel <= 3; channel++)
            for (int value = 0; value < 256; value++)
                table[(channel - 1) * 256 + value] = (float)(Value(Value(value, channel), 0) / 255);
        return table;
    }
}

/// <summary>Reads and writes the four curves as an array of arrays of handles.</summary>
/// <remarks>
/// A nested <see cref="EquatableListConverter{T}"/> would have to find metadata for the inner list,
/// and registering that inner list with the serializer context makes the context treat it as an
/// ordinary collection — which it cannot construct, since it is immutable. Writing the nesting out
/// here keeps the only registered element type the one that is genuinely a value: CurvePoint.
/// </remarks>
public sealed class CurveChannelsConverter : JsonConverter<EquatableList<EquatableList<CurvePoint>>>
{
    public override EquatableList<EquatableList<CurvePoint>> Read(
        ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an array");

        var handles = (JsonTypeInfo<CurvePoint>)options.GetTypeInfo(typeof(CurvePoint));
        var channels = new List<EquatableList<CurvePoint>>();

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected a curve");

            var points = new List<CurvePoint>();
            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                points.Add(JsonSerializer.Deserialize(ref reader, handles));

            channels.Add(new EquatableList<CurvePoint>(points));
        }

        return new EquatableList<EquatableList<CurvePoint>>(channels);
    }

    public override void Write(Utf8JsonWriter writer, EquatableList<EquatableList<CurvePoint>> value,
                               JsonSerializerOptions options)
    {
        var handles = (JsonTypeInfo<CurvePoint>)options.GetTypeInfo(typeof(CurvePoint));

        writer.WriteStartArray();
        foreach (EquatableList<CurvePoint> channel in value)
        {
            writer.WriteStartArray();
            foreach (CurvePoint point in channel) JsonSerializer.Serialize(writer, point, handles);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }
}
