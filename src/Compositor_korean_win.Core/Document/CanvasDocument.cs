using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>The blend modes a layer can composite with.</summary>
/// <remarks>
/// Versions 1 and 2 of the format could not store a blend mode at all, so a project declaring one
/// of those must leave every layer on <see cref="Normal"/>.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter<LayerBlendMode>))]
public enum LayerBlendMode
{
    [JsonStringEnumMemberName("Normal")] Normal,
    [JsonStringEnumMemberName("Multiply")] Multiply,
    [JsonStringEnumMemberName("Screen")] Screen,
    [JsonStringEnumMemberName("Overlay")] Overlay,
    [JsonStringEnumMemberName("Darken")] Darken,
    [JsonStringEnumMemberName("Lighten")] Lighten,
    [JsonStringEnumMemberName("Difference")] Difference,
    [JsonStringEnumMemberName("Color Dodge")] ColorDodge,
    [JsonStringEnumMemberName("Color Burn")] ColorBurn,
    [JsonStringEnumMemberName("Hue")] Hue,
    [JsonStringEnumMemberName("Saturation")] Saturation,
    [JsonStringEnumMemberName("Color")] Color,
    [JsonStringEnumMemberName("Luminosity")] Luminosity,
}

/// <summary>
/// A layer's mask: immutable, normalised, layer-local coverage. White reveals, black hides.
/// </summary>
/// <remarks>
/// The pixels are 8-bit grey with no alpha, and a uniform 1×1 mask is valid — that is how upstream
/// avoids allocating a full-resolution buffer for a mask nobody has painted on yet
/// (docs/windows-port.md §2.4).
/// </remarks>
public sealed record LayerMask
{
    public required PixelBuffer Coverage { get; init; }

    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Where the mask sits on the document once it has been moved apart from its layer; null while
    /// it covers the layer's own pixel grid and follows every change to it.
    /// </summary>
    public LayerTransform? Placement { get; init; }

    /// <summary>
    /// Linked, the layer and its mask transform together; unlinked, each moves on its own, as in
    /// Photoshop.
    /// </summary>
    public bool IsLinked { get; init; } = true;

    /// <summary>A mask that hides nothing, or one that hides everything, in a single pixel.</summary>
    public static LayerMask Solid(bool revealing)
    {
        // Grey in the colour channels with full alpha, as every mask is: the renderer reads the
        // coverage from the colour and treats no alpha as nothing there at all.
        PixelBuffer coverage = PixelBuffer.Allocate(1, 1);
        byte level = revealing ? (byte)255 : (byte)0;
        Span<byte> pixel = coverage.Row(0);
        pixel[0] = pixel[1] = pixel[2] = level;
        pixel[3] = 255;
        return new LayerMask { Coverage = coverage };
    }
}

/// <summary>One layer of a document: a placement, an optional raster, and how it composites.</summary>
/// <remarks>
/// Equality is by value except for the pixels, which compare by identity. That is deliberate and
/// load-bearing: history stores whole documents and decides whether an edit happened by comparing
/// them, so comparing a hundred megapixels byte by byte on every stroke is not an option — and
/// because buffers are immutable and shared (§2.2), identity is the right question anyway. Two
/// layers holding the same buffer hold the same pixels.
/// </remarks>
public sealed record ImageLayer
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required LayerTransform Transform { get; init; }

    /// <summary>The layer's pixels, or null for a blank layer or a folder.</summary>
    /// <remarks>
    /// A new blank layer has none: upstream allocates pixels when painting begins, not when the
    /// layer is added.
    /// </remarks>
    public PixelBuffer? Image { get; init; }

    public bool IsVisible { get; init; } = true;
    public Guid? ParentId { get; init; }
    public bool IsGroup { get; init; }
    public double Opacity { get; init; } = 1;
    public LayerBlendMode BlendMode { get; init; } = LayerBlendMode.Normal;

    /// <summary>The layer supplying live alpha — a clipping mask, in the interface's terms.</summary>
    public Guid? MaskSourceId { get; init; }

    public LayerMask? Mask { get; init; }
    public LayerAdjustment? Adjustment { get; init; }
    public LayerShapeStyle? Shape { get; init; }

    [JsonIgnore]
    public Point Origin => Transform.Origin;

    [JsonIgnore]
    public Size Size => Transform.Size;

    public bool Equals(ImageLayer? other) =>
        other is not null
        && Id == other.Id && Name == other.Name && IsVisible == other.IsVisible
        && Transform == other.Transform
        && ReferenceEquals(Image, other.Image)
        && ParentId == other.ParentId && IsGroup == other.IsGroup
        && Opacity.Equals(other.Opacity) && BlendMode == other.BlendMode
        && Mask == other.Mask && MaskSourceId == other.MaskSourceId
        && Adjustment == other.Adjustment && Shape == other.Shape;

    public override int GetHashCode() => HashCode.Combine(Id, Name, Transform, ParentId, Opacity, BlendMode);
}

/// <summary>A document: a fixed canvas and its layers, bottom to top.</summary>
public sealed record CanvasDocument
{
    public required Guid Id { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>Pixels per inch, 1–9600. Projects saved before this existed default to 72.</summary>
    public double Resolution { get; init; } = 72;

    /// <summary>Bottom to top. A group's children follow it as a contiguous subtree.</summary>
    public EquatableList<ImageLayer> Layers { get; init; } = EquatableList<ImageLayer>.Empty;

    [JsonIgnore]
    public Size Size => new(Width, Height);

    /// <summary>The canvas sizes the format accepts, for validating typed input.</summary>
    public static int? ValidDimension(string value) =>
        int.TryParse(value.Trim(), out int number) && number is >= 1 and <= 30_000 ? number : null;

    public ImageLayer? Layer(Guid id) => Layers.FirstOrDefault(layer => layer.Id == id);

    public int IndexOf(Guid id)
    {
        for (int i = 0; i < Layers.Count; i++)
            if (Layers[i].Id == id) return i;
        return -1;
    }

    /// <summary>The same document with one layer replaced.</summary>
    public CanvasDocument Replacing(ImageLayer layer)
    {
        int index = IndexOf(layer.Id);
        if (index < 0) throw new ArgumentException($"no layer {layer.Id} in this document", nameof(layer));
        return this with { Layers = Layers.With(index, layer) };
    }
}
