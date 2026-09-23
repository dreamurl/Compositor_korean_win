using System.Diagnostics;
using Compositor_korean_win.Core;

namespace Compositor_korean_win.Shell;

/// <summary>
/// M7's check: in the AI build, the model loads, runs, and finds the subject of a picture that has
/// an obvious one; in the plain build, it reports the model absent and nothing fails.
/// </summary>
/// <remarks>
/// The picture is made here rather than shipped: a dark disc on a pale ground, which any
/// segmentation model calls a subject. The logits go through the same <see cref="SubjectMatte"/>
/// the filter uses, so a model whose output needs no sigmoid, or planes in the wrong order, shows
/// up as a disc that is not white.
/// </remarks>
internal static class AiCheck
{
    internal sealed record Result(bool Installed, string Device, double LoadMs, double RunMs,
                                  double Inside, double Outside, string? Error)
    {
        public bool Passed => !Installed || Error is null && Inside > 0.6 && Outside < 0.4;
    }

    public static Result Run()
    {
        if (!OnnxSubjectModel.Availability) return new Result(false, "none", 0, 0, 0, 0, null);

        var clock = Stopwatch.StartNew();
        using OnnxSubjectModel? model = OnnxSubjectModel.Load(out string? error);
        double loadMs = clock.Elapsed.TotalMilliseconds;
        if (model is null) return new Result(true, "none", loadMs, 0, 0, 0, error ?? "did not load");

        const int Width = 640, Height = 480, Radius = 130;
        using PixelBuffer picture = Disc(Width, Height, Radius);

        clock.Restart();
        float[] logits = model.Predict(SubjectMatte.Input(picture, model.Side));
        double runMs = clock.Elapsed.TotalMilliseconds;

        using PixelBuffer mask = SubjectMatte.Mask(logits, model.Side, Width, Height);
        double inside = 0, outside = 0;
        int insideCount = 0, outsideCount = 0;
        for (int y = 0; y < Height; y++)
        {
            ReadOnlySpan<byte> row = mask.Row(y);
            for (int x = 0; x < Width; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x - Width / 2.0, 2) + Math.Pow(y - Height / 2.0, 2));
                // A margin either side of the rim, where any model is allowed to be unsure.
                if (distance < Radius - 12) { inside += row[x * 4] / 255.0; insideCount++; }
                else if (distance > Radius + 12) { outside += row[x * 4] / 255.0; outsideCount++; }
            }
        }

        return new Result(true, model.Device, loadMs, runMs, inside / insideCount, outside / outsideCount, null);
    }

    private static PixelBuffer Disc(int width, int height, int radius)
    {
        PixelBuffer picture = PixelBuffer.Allocate(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = picture.Row(y);
            for (int x = 0; x < width; x++)
            {
                double distance = Math.Sqrt(Math.Pow(x - width / 2.0, 2) + Math.Pow(y - height / 2.0, 2));
                bool disc = distance <= radius;
                row[x * 4] = disc ? (byte)150 : (byte)225;
                row[x * 4 + 1] = disc ? (byte)30 : (byte)228;
                row[x * 4 + 2] = disc ? (byte)40 : (byte)232;
                row[x * 4 + 3] = 255;
            }
        }
        return picture;
    }
}
