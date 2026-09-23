namespace Compositor_korean_win.Core;

/// <summary>
/// Edits to the document as a whole: a new one, its canvas size, its image size, and images brought
/// in as layers.
/// </summary>
/// <remarks>
/// <para>
/// Canvas Size moves nothing but the frame: every layer keeps its pixels and shifts by the anchor's
/// share of the change, so the picture stays put relative to the anchored edge. Image Size resamples:
/// each layer is drawn into its new box at the new scale, as upstream's <c>ImageResizer</c> does,
/// through the same projective resampler the distortion drag commits with (<see cref="QuadWarp"/>),
/// which takes a non-uniform scale of a turned layer — a shear no placement can hold — in its stride.
/// </para>
/// <para>
/// The limits are the format's: thirty thousand pixels a side, a hundred million in all.
/// </para>
/// </remarks>
public static class DocumentCommands
{
    public const int MaximumSide = 30_000;
    public const long MaximumPixels = 100_000_000;

    /// <summary>Whether a canvas of this size can be made.</summary>
    public static bool IsValidSize(int width, int height) =>
        width is >= 1 and <= MaximumSide && height is >= 1 and <= MaximumSide && (long)width * height <= MaximumPixels;

    /// <summary>
    /// A new document with one layer: filled with <paramref name="background"/>, or blank for a
    /// transparent canvas.
    /// </summary>
    public static CanvasDocument New(int width, int height, double resolution, Rgba? background)
    {
        if (!IsValidSize(width, height)) throw new ArgumentOutOfRangeException(nameof(width), "not a canvas size the format allows");

        var layer = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = background is null
                ? Localizer.Format(TextKey.LayerNameNumbered, 1)
                : Localizer.Text(TextKey.LayerBackground),
            Transform = new LayerTransform(Point.Zero, new Size(width, height)),
        };

        if (background is Rgba colour)
        {
            PixelBuffer pixels = PixelBuffer.Allocate(width, height);
            Span<byte> first = pixels.Row(0);
            for (int x = 0; x < width; x++)
            {
                double a = colour.A / 255.0;
                first[x * 4] = (byte)Math.Round(colour.R * a);
                first[x * 4 + 1] = (byte)Math.Round(colour.G * a);
                first[x * 4 + 2] = (byte)Math.Round(colour.B * a);
                first[x * 4 + 3] = colour.A;
            }
            for (int y = 1; y < height; y++) first.CopyTo(pixels.Row(y));
            layer = layer with { Image = pixels };
        }

        return new CanvasDocument
        {
            Id = Guid.NewGuid(),
            Width = width,
            Height = height,
            Resolution = Math.Clamp(resolution, 1, 9600),
            Layers = new EquatableList<ImageLayer>([layer]),
        };
    }

    /// <summary>
    /// How far the content moves when the canvas goes from one size to another, for an anchor
    /// numbered row by row from the top left (0) to the bottom right (8).
    /// </summary>
    /// <remarks>Upstream's rounding: an odd extra pixel goes right and down.</remarks>
    public static Point AnchorOffset(int fromWidth, int fromHeight, int toWidth, int toHeight, int anchor)
    {
        anchor = Math.Clamp(anchor, 0, 8);
        return new Point(Math.Floor((toWidth - fromWidth) * (anchor % 3) / 2.0),
                         Math.Floor((toHeight - fromHeight) * (anchor / 3) / 2.0));
    }

    /// <summary>
    /// The canvas made a new size, the content shifted to keep to the anchor. With an
    /// <paramref name="extension"/> colour, the space a larger canvas adds is filled with it, on a
    /// layer of its own at the bottom — upstream's "Canvas Extension", which keeps the old content's
    /// layers as they were.
    /// </summary>
    public static CanvasDocument? ResizeCanvas(CanvasDocument document, int width, int height, int anchor, Rgba? extension = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsValidSize(width, height) || (width == document.Width && height == document.Height)) return null;

        return Shifted(document, width, height, AnchorOffset(document.Width, document.Height, width, height, anchor), extension);
    }

    /// <summary>
    /// Image › Crop, or the Crop tool's Apply: the canvas becomes <paramref name="frame"/>, in the
    /// old canvas's pixels, and every layer moves with it — upstream's crop, a canvas resize with the
    /// content offset. Nothing is cut from any layer, so what falls outside the frame is still
    /// there to move back in; a frame reaching past the canvas grows it.
    /// </summary>
    public static CanvasDocument? Crop(CanvasDocument document, PixelRect frame)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!IsValidSize(frame.Width, frame.Height)) return null;
        if (frame.X == 0 && frame.Y == 0 && frame.Width == document.Width && frame.Height == document.Height) return null;
        return Shifted(document, frame.Width, frame.Height, new Point(-frame.X, -frame.Y), extension: null);
    }

    private static CanvasDocument Shifted(CanvasDocument document, int width, int height, Point offset, Rgba? extension)
    {
        LayerTransform Moved(LayerTransform transform) =>
            transform with { Origin = new Point(transform.Origin.X + offset.X, transform.Origin.Y + offset.Y) };

        List<ImageLayer> layers = [.. document.Layers.Select(layer => layer with
        {
            Transform = Moved(layer.Transform),
            Mask = layer.Mask is { Placement: LayerTransform placement } mask ? mask with { Placement = Moved(placement) } : layer.Mask,
        })];

        if (extension is Rgba colour && (width > document.Width || height > document.Height))
        {
            layers.Insert(0, new ImageLayer
            {
                Id = Guid.NewGuid(),
                Name = Localizer.Text(TextKey.LayerCanvasExtension),
                Transform = new LayerTransform(Point.Zero, new Size(width, height)),
                Image = Extension(width, height, colour,
                                  new PixelRect((int)offset.X, (int)offset.Y, document.Width, document.Height)),
            });
        }

        return document with { Width = width, Height = height, Layers = layers.ToEquatableList() };
    }

    /// <summary>A canvas of one colour with the old canvas's place left clear.</summary>
    private static PixelBuffer Extension(int width, int height, Rgba colour, PixelRect old)
    {
        PixelBuffer pixels = PixelBuffer.Allocate(width, height);
        double a = colour.A / 255.0;
        byte r = (byte)Math.Round(colour.R * a), g = (byte)Math.Round(colour.G * a), b = (byte)Math.Round(colour.B * a);

        for (int y = 0; y < height; y++)
        {
            Span<byte> row = pixels.Row(y);
            bool inside = y >= old.Y && y < old.Y + old.Height;
            for (int x = 0; x < width; x++)
            {
                if (inside && x >= old.X && x < old.X + old.Width) continue;
                row[x * 4] = r;
                row[x * 4 + 1] = g;
                row[x * 4 + 2] = b;
                row[x * 4 + 3] = colour.A;
            }
        }
        return pixels;
    }

    /// <summary>
    /// The whole document resampled to a new size: every layer redrawn into the box its corners
    /// land in, upright, and every mask with it. Null when the size is the same or not allowed.
    /// </summary>
    public static CanvasDocument? ResizeImage(CanvasDocument document, int width, int height, double resolution)
    {
        if (!IsValidSize(width, height)) return null;
        if (width == document.Width && height == document.Height)
            return resolution == document.Resolution ? null : document with { Resolution = Math.Clamp(resolution, 1, 9600) };

        double sx = (double)width / document.Width, sy = (double)height / document.Height;
        var layers = new List<ImageLayer>(document.Layers.Count);
        var made = new List<PixelBuffer>();

        try
        {
            foreach (ImageLayer layer in document.Layers)
            {
                Point[] corners =
                [
                    Scaled(layer.Transform.PointAt(new Point(0, 0))), Scaled(layer.Transform.PointAt(new Point(1, 0))),
                    Scaled(layer.Transform.PointAt(new Point(1, 1))), Scaled(layer.Transform.PointAt(new Point(0, 1))),
                ];

                // The corners of the layer's own grid, in the order QuadWarp takes them — which a
                // flip reverses along its axis.
                Point[] warp = Unflipped(corners, layer.Transform);
                PixelRect box = Rect.Around(corners).Enclosing();
                var upright = new LayerTransform(new Point(box.X, box.Y), new Size(Math.Max(1, box.Width), Math.Max(1, box.Height)));

                ImageLayer resized = layer with { Transform = upright };

                if (layer.Image is PixelBuffer image)
                {
                    if (QuadWarp.Resample(image, warp) is not (PixelBuffer pixels, LayerTransform placement)) return Abandon();
                    made.Add(pixels);
                    resized = resized with { Image = pixels, Transform = placement };
                }

                if (layer.Mask is LayerMask mask)
                {
                    if (mask.Placement is LayerTransform maskPlacement)
                    {
                        resized = resized with { Mask = mask with { Placement = ScaledBox(maskPlacement) } };
                    }
                    else if (mask.Coverage.Width > 1 || mask.Coverage.Height > 1)
                    {
                        // A painted mask follows its layer into the new grid; a uniform one is the same
                        // at any size and is kept as it is.
                        if (QuadWarp.Resample(mask.Coverage, warp) is not (PixelBuffer coverage, LayerTransform _)) return Abandon();
                        made.Add(coverage);
                        resized = resized with { Mask = mask with { Coverage = Opaque(coverage) } };
                    }
                }

                layers.Add(resized);
            }
        }
        catch
        {
            foreach (PixelBuffer buffer in made) buffer.Release();
            throw;
        }

        return document with
        {
            Width = width,
            Height = height,
            Resolution = Math.Clamp(resolution, 1, 9600),
            Layers = layers.ToEquatableList(),
        };

        Point Scaled(Point point) => new(point.X * sx, point.Y * sy);

        // A layer that cannot be resampled leaves the document as it was, and nothing made for it behind.
        CanvasDocument? Abandon()
        {
            foreach (PixelBuffer buffer in made) buffer.Release();
            return null;
        }

        LayerTransform ScaledBox(LayerTransform transform) => transform with
        {
            Origin = Scaled(transform.Origin),
            Size = new Size(transform.Size.Width * sx, transform.Size.Height * sy),
        };
    }

    /// <summary>
    /// An image brought in as a new layer above <paramref name="active"/>, centred on the canvas at
    /// its own size.
    /// </summary>
    public static (CanvasDocument Document, Guid Layer) AddImage(CanvasDocument document, PixelBuffer pixels,
                                                                 string name, Guid? active)
    {
        ImageLayer? above = active is Guid id ? document.Layer(id) : null;
        var layer = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = name,
            Image = pixels,
            Transform = new LayerTransform(
                new Point(Math.Round((document.Width - pixels.Width) / 2.0), Math.Round((document.Height - pixels.Height) / 2.0)),
                new Size(pixels.Width, pixels.Height)),
            ParentId = above is { IsGroup: true } ? above.Id : above?.ParentId,
        };

        var layers = document.Layers.ToList();
        layers.Insert(above is null ? layers.Count : document.IndexOf(above.Id) + 1, layer);
        return (document with { Layers = layers.ToEquatableList() }, layer.Id);
    }

    /// <summary>
    /// A layer's corners in the order of its own pixel grid: (0,0), (1,0), (1,1), (0,1) of the pixels
    /// rather than of the placed box, which a flip mirrors.
    /// </summary>
    private static Point[] Unflipped(Point[] corners, LayerTransform transform)
    {
        Point[] result = corners;
        if (transform.FlipX) result = [result[1], result[0], result[3], result[2]];
        if (transform.FlipY) result = [result[3], result[2], result[1], result[0]];
        return result;
    }

    /// <summary>A resampled mask's edge is transparent where it faded; a mask must stay opaque.</summary>
    private static PixelBuffer Opaque(PixelBuffer coverage)
    {
        for (int y = 0; y < coverage.Height; y++)
        {
            Span<byte> row = coverage.Row(y);
            for (int x = 0; x < coverage.Width; x++)
            {
                int alpha = row[x * 4 + 3];
                if (alpha is 0 or 255) { row[x * 4 + 3] = 255; continue; }

                // Coverage lives in the colour, premultiplied along with it; lift it back out.
                for (int c = 0; c < 3; c++) row[x * 4 + c] = (byte)Math.Min(255, row[x * 4 + c] * 255 / alpha);
                row[x * 4 + 3] = 255;
            }
        }
        return coverage;
    }
}
