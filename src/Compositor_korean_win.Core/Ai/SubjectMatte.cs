namespace Compositor_korean_win.Core;

/// <summary>
/// A segmentation model that says where the subject of an image is — what upstream asks Vision
/// for (<c>VNGenerateForegroundInstanceMaskRequest</c>) and this port asks an ONNX model.
/// </summary>
/// <remarks>
/// Core knows only this shape; the runtime that answers it lives in the shell, so the app builds,
/// tests and runs without it (docs/windows-port.md 7). The model takes one square image as three
/// normalised planes and gives back one plane of logits the same size.
/// </remarks>
public interface ISubjectModel
{
    /// <summary>The side of the square the model works at.</summary>
    int Side { get; }

    /// <summary>Where it runs, for the report: "DirectML", "CPU".</summary>
    string Device { get; }

    /// <summary>Logits for <paramref name="planes"/> — R, G, B planes of <see cref="Side"/>², normalised.</summary>
    float[] Predict(float[] planes);
}

/// <summary>
/// Getting an image into a subject model and its answer back out as a mask: the parts of
/// background removal that are arithmetic rather than a model.
/// </summary>
/// <remarks>
/// <para>
/// BiRefNet's preprocessing, as its <c>preprocessor_config.json</c> gives it: the image squeezed to
/// the model's square whatever its shape, scaled to 0–1 and normalised with ImageNet's mean and
/// deviation. Its output is logits, so the way back is a sigmoid and the square stretched back over
/// the image.
/// </para>
/// <para>
/// A big layer is halved first rather than sampled straight down to 1024: one bilinear tap per
/// output pixel would read four pixels out of a hundred and alias, the same reason every other
/// reduction here goes through the pyramid.
/// </para>
/// </remarks>
public static class SubjectMatte
{
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Deviation = [0.229f, 0.224f, 0.225f];

    /// <summary>The image as the model's input planes, <paramref name="side"/> square.</summary>
    /// <remarks>
    /// Transparent pixels are seen over black, which is what unpremultiplied RGB of nothing is and
    /// what the reference pipeline would feed it from an RGBA image with its alpha dropped.
    /// </remarks>
    public static float[] Input(PixelBuffer image, int side)
    {
        PixelBuffer source = image.Retain();
        try
        {
            while (source.Width >= side * 2 && source.Height >= side * 2)
            {
                PixelBuffer half = DownsamplePyramid.Halve(source);
                source.Release();
                source = half;
            }

            var planes = new float[3 * side * side];
            int plane = side * side;
            double sx = (double)source.Width / side, sy = (double)source.Height / side;

            for (int y = 0; y < side; y++)
            {
                double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, source.Height - 1);
                int y0 = (int)fy, y1 = Math.Min(source.Height - 1, y0 + 1);
                float ty = (float)(fy - y0);
                ReadOnlySpan<byte> top = source.Row(y0), bottom = source.Row(y1);

                for (int x = 0; x < side; x++)
                {
                    double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, source.Width - 1);
                    int x0 = (int)fx, x1 = Math.Min(source.Width - 1, x0 + 1);
                    float tx = (float)(fx - x0);

                    for (int c = 0; c < 3; c++)
                    {
                        float a = top[x0 * 4 + c] + (top[x1 * 4 + c] - top[x0 * 4 + c]) * tx;
                        float b = bottom[x0 * 4 + c] + (bottom[x1 * 4 + c] - bottom[x0 * 4 + c]) * tx;
                        float level = (a + (b - a) * ty) / 255f;
                        planes[c * plane + y * side + x] = (level - Mean[c]) / Deviation[c];
                    }
                }
            }

            return planes;
        }
        finally
        {
            source.Release();
        }
    }

    /// <summary>
    /// The model's logits as a mask over an image of <paramref name="width"/> × <paramref name="height"/>:
    /// grey, white over the subject, opaque — the layout every mask here has.
    /// </summary>
    public static PixelBuffer Mask(float[] logits, int side, int width, int height)
    {
        if (logits.Length < side * side) throw new ArgumentException("one logit per model pixel", nameof(logits));

        var levels = new float[side * side];
        for (int i = 0; i < levels.Length; i++) levels[i] = 1f / (1f + MathF.Exp(-logits[i]));

        PixelBuffer mask = PixelBuffer.Allocate(width, height);
        double sx = (double)side / width, sy = (double)side / height;
        for (int y = 0; y < height; y++)
        {
            double fy = Math.Clamp((y + 0.5) * sy - 0.5, 0, side - 1);
            int y0 = (int)fy, y1 = Math.Min(side - 1, y0 + 1);
            float ty = (float)(fy - y0);
            Span<byte> row = mask.Row(y);

            for (int x = 0; x < width; x++)
            {
                double fx = Math.Clamp((x + 0.5) * sx - 0.5, 0, side - 1);
                int x0 = (int)fx, x1 = Math.Min(side - 1, x0 + 1);
                float tx = (float)(fx - x0);

                float a = levels[y0 * side + x0] + (levels[y0 * side + x1] - levels[y0 * side + x0]) * tx;
                float b = levels[y1 * side + x0] + (levels[y1 * side + x1] - levels[y1 * side + x0]) * tx;
                byte level = (byte)Math.Clamp(MathF.Round((a + (b - a) * ty) * 255), 0, 255);
                row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = level;
                row[x * 4 + 3] = 255;
            }
        }

        return mask;
    }
}
