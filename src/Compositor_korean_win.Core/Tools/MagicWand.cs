namespace Compositor_korean_win.Core;

/// <summary>How much a pixel may differ from the one clicked and still be selected.</summary>
public sealed record WandSettings
{
    /// <summary>0–255, per channel, alpha included.</summary>
    public int Tolerance { get; init; } = 32;

    /// <summary>Whether the selection has to reach the seed without leaving matching pixels.</summary>
    public bool Contiguous { get; init; } = true;

    /// <summary>Half-width of the square averaged to get the colour to match against.</summary>
    public int SampleRadius { get; init; }
}

/// <summary>What came of a wand click.</summary>
public enum WandOutcome
{
    Selected,

    /// <summary>The click was outside the layer, or nothing was close enough to it.</summary>
    NothingMatched,

    /// <summary>The shape has more edges than are worth outlining.</summary>
    TooDetailed,

    OutOfMemory,
}

/// <summary>
/// Selecting pixels that look like the one clicked.
/// </summary>
/// <remarks>
/// Both halves are upstream's C (<c>WandPixels.c</c>), reused unchanged: matching walks every
/// pixel of the layer and tracing walks every edge between a selected pixel and an unselected one.
/// The outline comes back along exact pixel edges, with outer boundaries wound one way and holes
/// the other, so the winding rule fills precisely the matched pixels — which is why the selection
/// it produces needs no antialiasing and should not be given any.
/// </remarks>
public static class MagicWand
{
    /// <summary>The selection matching the layer pixel under <paramref name="point"/>.</summary>
    public static unsafe (DocumentSelection? Selection, WandOutcome Outcome) Select(
        PixelBuffer image, Point point, WandSettings settings)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return (null, WandOutcome.NothingMatched);

        int x = (int)Math.Floor(point.X), y = (int)Math.Floor(point.Y);
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return (null, WandOutcome.NothingMatched);

        var mask = new byte[(long)image.Width * image.Height];
        long count;

        fixed (byte* marked = mask)
        {
            count = Kernels.WandMask(image.Scan0, (nuint)image.Width, (nuint)image.Height, (nuint)image.Stride,
                                     (nuint)x, (nuint)y, (nuint)Math.Max(0, settings.SampleRadius),
                                     Math.Clamp(settings.Tolerance, 0, 255),
                                     settings.Contiguous ? 1 : 0, (nint)marked);
        }

        if (count < 0) return (null, WandOutcome.OutOfMemory);
        if (count == 0) return (null, WandOutcome.NothingMatched);

        return Outline(mask, image.Width, image.Height);
    }

    /// <summary>The outline of a mask's nonzero pixels, as a selection.</summary>
    public static unsafe (DocumentSelection? Selection, WandOutcome Outcome) Outline(
        byte[] mask, int width, int height)
    {
        if (width <= 0 || height <= 0 || mask.LongLength != (long)width * height)
            return (null, WandOutcome.NothingMatched);

        int status;
        nint points, loops;
        nuint pointCount, loopCount;

        fixed (byte* marked = mask)
        {
            status = Kernels.WandTrace((nint)marked, (nuint)width, (nuint)height,
                                       out points, out pointCount, out loops, out loopCount);
        }

        try
        {
            if (status == -2) return (null, WandOutcome.TooDetailed);
            if (status != 0) return (null, WandOutcome.OutOfMemory);
            if (loopCount == 0 || points == 0 || loops == 0) return (null, WandOutcome.NothingMatched);

            var outlines = new List<SelectionLoop>((int)loopCount);
            int* corner = (int*)points;
            int* lengths = (int*)loops;
            int index = 0;

            for (int loop = 0; loop < (int)loopCount; loop++)
            {
                int length = lengths[loop];
                if (length < 3) { index += length; continue; }

                var shape = new Point[length];
                for (int i = 0; i < length; i++)
                {
                    shape[i] = new Point(corner[(index + i) * 2], corner[(index + i) * 2 + 1]);
                }

                index += length;
                outlines.Add(new SelectionLoop(shape));
            }

            if (outlines.Count == 0) return (null, WandOutcome.NothingMatched);

            return (new DocumentSelection
            {
                Shapes = [new SelectionShape(SelectionOperation.Add, outlines)],

                // The outline runs along pixel edges, so it is exact as it stands; softening it
                // would blur a boundary the kernel measured to the pixel.
                IsAntialiased = false,
            }, WandOutcome.Selected);
        }
        finally
        {
            Kernels.Free(points);
            Kernels.Free(loops);
        }
    }
}
