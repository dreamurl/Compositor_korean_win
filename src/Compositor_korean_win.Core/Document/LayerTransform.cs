using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>How a layer's pixels are resampled when its transform does not place them 1:1.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<LayerSampling>))]
public enum LayerSampling
{
    [JsonStringEnumMemberName("Nearest")] Nearest,
    [JsonStringEnumMemberName("Smooth")] Smooth,
    [JsonStringEnumMemberName("High quality")] High,
}

/// <summary>A point in document pixels. Serialises as CoreGraphics' CGPoint does.</summary>
public readonly record struct Point(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y)
{
    public static Point Zero => new(0, 0);
}

/// <summary>A size in document pixels. Serialises as CoreGraphics' CGSize does.</summary>
public readonly record struct Size(
    [property: JsonPropertyName("width")] double Width,
    [property: JsonPropertyName("height")] double Height);

/// <summary>
/// Where a layer sits on the document: unrotated bounds in document pixels, with rotation
/// clockwise around their centre.
/// </summary>
/// <remarks>
/// This is the record that makes upstream's transforms non-destructive, and the reason a layer
/// scaled down to a thumbnail still holds its full resolution (docs/windows-port.md §2.1): moving,
/// scaling, rotating and flipping only ever rewrite these six values, never the pixels.
/// </remarks>
public sealed record LayerTransform
{
    [JsonPropertyName("origin")] public Point Origin { get; init; }
    [JsonPropertyName("size")] public Size Size { get; init; }
    [JsonPropertyName("rotation")] public double Rotation { get; init; }
    [JsonPropertyName("flipX")] public bool FlipX { get; init; }
    [JsonPropertyName("flipY")] public bool FlipY { get; init; }
    [JsonPropertyName("sampling")] public LayerSampling Sampling { get; init; } = LayerSampling.High;

    public LayerTransform() { }

    public LayerTransform(Point origin, Size size)
    {
        Origin = origin;
        Size = size;
    }

    [JsonIgnore]
    public Point Center => new(Origin.X + Size.Width / 2, Origin.Y + Size.Height / 2);

    [JsonIgnore]
    public double Radians => Rotation % 360 * Math.PI / 180;

    /// <summary>
    /// The bounds upstream's <c>ProjectStore</c> accepts. A project that fails this is rejected
    /// before it can replace the open document.
    /// </summary>
    [JsonIgnore]
    public bool IsValid =>
        double.IsFinite(Origin.X) && double.IsFinite(Origin.Y)
        && double.IsFinite(Size.Width) && double.IsFinite(Size.Height) && double.IsFinite(Rotation)
        && Size.Width is >= 1 and <= 300_000 && Size.Height is >= 1 and <= 300_000
        && Math.Abs(Origin.X) <= 1_000_000 && Math.Abs(Origin.Y) <= 1_000_000;

    /// <summary>Whether this placement can be drawn at all.</summary>
    /// <remarks>
    /// Looser than <see cref="IsValid"/> on purpose. That one is the format's rule about what may be
    /// stored, and a minimum of one pixel a side belongs there. A viewport projection produces
    /// placements the format would quite rightly refuse — a hundred-pixel layer is a tenth of a
    /// pixel wide once the document is zoomed out to a thumbnail — and those still have to draw,
    /// or small layers would disappear as the canvas is zoomed out.
    /// </remarks>
    [JsonIgnore]
    public bool IsDrawable =>
        double.IsFinite(Origin.X) && double.IsFinite(Origin.Y) && double.IsFinite(Rotation)
        && double.IsFinite(Size.Width) && double.IsFinite(Size.Height)
        && Size.Width > 0 && Size.Height > 0;

    /// <summary>Where the unit square's <paramref name="unit"/> lands on the document.</summary>
    public Point PointAt(Point unit)
    {
        double x = (unit.X - 0.5) * Size.Width;
        double y = (unit.Y - 0.5) * Size.Height;
        double cos = Math.Cos(Radians), sin = Math.Sin(Radians);
        return new Point(Center.X + x * cos - y * sin, Center.Y + x * sin + y * cos);
    }

    /// <summary>Where a document point falls on the unit square — the inverse of <see cref="PointAt"/>.</summary>
    public Point UnitAt(Point point)
    {
        double x = point.X - Center.X, y = point.Y - Center.Y;
        double cos = Math.Cos(Radians), sin = Math.Sin(Radians);
        return new Point((x * cos + y * sin) / Size.Width + 0.5, (-x * sin + y * cos) / Size.Height + 0.5);
    }

    /// <summary>Whether <paramref name="point"/> falls inside this rotated rectangle.</summary>
    public bool Contains(Point point)
    {
        double x = point.X - Center.X, y = point.Y - Center.Y;
        double cos = Math.Cos(Radians), sin = Math.Sin(Radians);
        return Math.Abs(x * cos + y * sin) <= Size.Width / 2
            && Math.Abs(-x * sin + y * cos) <= Size.Height / 2;
    }

    /// <summary>Width as a percentage of the pixels it places; 100% draws them 1:1.</summary>
    public double ScalePercent(Size pixelSize) => Size.Width / Math.Max(1, pixelSize.Width) * 100;

    /// <summary>Both sides set to a percentage of <paramref name="pixelSize"/>, keeping the centre.</summary>
    public LayerTransform ScaledToPercent(double percent, Size pixelSize)
    {
        var size = new Size(pixelSize.Width * percent / 100, pixelSize.Height * percent / 100);
        return this with
        {
            Size = size,
            Origin = new Point(Center.X - size.Width / 2, Center.Y - size.Height / 2),
        };
    }

    /// <summary>
    /// Whole pixels and whole degrees — what dragging, scaling and rotating leave behind.
    /// </summary>
    /// <remarks>
    /// Typed values are used as they are, so a fraction can still be asked for by hand.
    /// </remarks>
    public LayerTransform Rounded() => this with
    {
        Origin = new Point(Math.Round(Origin.X, MidpointRounding.AwayFromZero),
                           Math.Round(Origin.Y, MidpointRounding.AwayFromZero)),
        Size = new Size(Math.Max(1, Math.Round(Size.Width, MidpointRounding.AwayFromZero)),
                        Math.Max(1, Math.Round(Size.Height, MidpointRounding.AwayFromZero))),
        Rotation = Math.Round(Rotation, MidpointRounding.AwayFromZero),
    };

    /// <summary>This placement carried along as a layer moves from <paramref name="from"/> to <paramref name="to"/>.</summary>
    /// <remarks>
    /// An unlinked mask keeps its own placement while its layer is transformed; this is how that
    /// placement follows. A plain move carries exactly, which is the common case and worth keeping
    /// free of rounding.
    /// </remarks>
    public LayerTransform Following(LayerTransform from, LayerTransform to)
    {
        if (from == to) return this;

        if (from.Size == to.Size && from.Rotation == to.Rotation
            && from.FlipX == to.FlipX && from.FlipY == to.FlipY)
        {
            return this with
            {
                Origin = new Point(Origin.X + to.Origin.X - from.Origin.X,
                                   Origin.Y + to.Origin.Y - from.Origin.Y),
            };
        }

        // Anything else is a scale or a rotation, so the placement scales and rotates about the
        // layer's centre by the same amount.
        double scaleX = from.Size.Width == 0 ? 1 : to.Size.Width / from.Size.Width;
        double scaleY = from.Size.Height == 0 ? 1 : to.Size.Height / from.Size.Height;
        double turn = (to.Rotation - from.Rotation) * Math.PI / 180;
        double cos = Math.Cos(turn), sin = Math.Sin(turn);

        double relativeX = (Center.X - from.Center.X) * scaleX;
        double relativeY = (Center.Y - from.Center.Y) * scaleY;
        var center = new Point(to.Center.X + relativeX * cos - relativeY * sin,
                               to.Center.Y + relativeX * sin + relativeY * cos);
        var size = new Size(Size.Width * scaleX, Size.Height * scaleY);

        return this with
        {
            Size = size,
            Origin = new Point(center.X - size.Width / 2, center.Y - size.Height / 2),
            Rotation = Rotation + (to.Rotation - from.Rotation),
            FlipX = FlipX ^ (from.FlipX != to.FlipX),
            FlipY = FlipY ^ (from.FlipY != to.FlipY),
        };
    }
}
