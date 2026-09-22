namespace Compositor_korean_win.Core;

/// <summary>
/// A renderer with no GPU and no window behind it.
/// </summary>
/// <remarks>
/// <para>
/// This is the reference the Direct2D backend is checked against. docs/windows-port.md §4.2 asks
/// for the boundary partly so that the Core tests can run without a backend; having a second
/// implementation makes that concrete, and it means the compositing rules — which modes blend how,
/// how masks multiply, what a clipping group does to a soft edge — are pinned by tests that cannot
/// drift with a driver update.
/// </para>
/// <para>
/// It works by inverse mapping: for every destination pixel, where did it come from. That is the
/// straightforward way to get rotation, flipping and resampling right at once, and it makes the
/// pyramid fall out naturally — the level is chosen from how many source pixels one destination
/// pixel covers, and the sample is taken from that level.
/// </para>
/// </remarks>
public sealed class SoftwareRenderBackend : IRenderBackend
{
    private readonly DownsamplePyramid _pyramid = new();

    public string Name => "software";

    public IRenderSurface CreateSurface(int width, int height) => new Surface(width, height, _pyramid);

    public PixelBuffer Downsample(PixelBuffer source, int level)
    {
        (PixelBuffer image, int _) = _pyramid.Reduced(source, level);
        // The cache owns its copies, so hand back one the caller may keep.
        return PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height));
    }

    public void Dispose() => _pyramid.Dispose();

    private sealed class Surface(int width, int height, DownsamplePyramid pyramid) : IRenderSurface
    {
        private readonly PixelBuffer _pixels = PixelBuffer.Allocate(width, height);
        private readonly Stack<PixelRect> _clips = new();

        public int Width => width;
        public int Height => height;

        /// <summary>What draws may touch: the innermost clip, or the whole surface.</summary>
        private PixelRect Clip => _clips.Count > 0 ? _clips.Peek() : new PixelRect(0, 0, Width, Height);

        public void PushClip(Rect region) => _clips.Push(region.Rounded().Intersect(Clip));

        public void PopClip()
        {
            if (_clips.Count > 0) _clips.Pop();
        }

        public void Clear()
        {
            for (int y = 0; y < Height; y++) _pixels.Row(y).Clear();
        }

        public PixelBuffer Read() => PixelRegion.Copy(_pixels, new PixelRect(0, 0, Width, Height));

        public void Write(PixelBuffer pixels)
        {
            if (pixels.Width != Width || pixels.Height != Height)
                throw new ArgumentException("not the surface's size", nameof(pixels));

            for (int y = 0; y < Height; y++) pixels.Row(y).CopyTo(_pixels.Row(y));
        }

        public void Draw(LayerDraw draw)
        {
            LayerTransform placement = draw.Placement;
            if (!placement.IsDrawable || draw.Opacity <= 0) return;

            PixelRect area = LayerGeometry.Bounds(placement).Intersect(Clip);
            if (area.IsEmpty) return;

            // A layer drawn smaller than its pixels reads from a halved copy instead of throwing
            // most of them away.
            int level = LayerGeometry.LevelFor(placement, draw.Source.Width);

            using var source = new SampledSource(draw.Source, pyramid, level, area, placement);

            double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
            Point center = placement.Center;
            double halfWidth = placement.Size.Width / 2, halfHeight = placement.Size.Height / 2;
            bool nearest = placement.Sampling == LayerSampling.Nearest;

            Span<byte> pixel = stackalloc byte[4];

            for (int y = area.Y; y < area.Bottom; y++)
            {
                Span<byte> row = _pixels.Row(y);

                for (int x = area.X; x < area.Right; x++)
                {
                    // Undo the placement: rotate back around the centre, then undo the flips.
                    double dx = x + 0.5 - center.X, dy = y + 0.5 - center.Y;
                    double localX = dx * cos + dy * sin;
                    double localY = -dx * sin + dy * cos;
                    if (placement.FlipX) localX = -localX;
                    if (placement.FlipY) localY = -localY;

                    if (Math.Abs(localX) > halfWidth || Math.Abs(localY) > halfHeight) continue;

                    double u = localX / placement.Size.Width + 0.5;
                    double v = localY / placement.Size.Height + 0.5;

                    source.Sample(u, v, nearest, pixel);
                    if (pixel[3] == 0) continue;

                    double coverage = draw.Opacity;
                    if (draw.Mask is PixelBuffer mask) coverage *= SampleCoverage(mask, u, v, nearest);
                    if (coverage <= 0) continue;

                    foreach (MaskClip clip in draw.Clips)
                    {
                        coverage *= SampleClip(clip, x + 0.5, y + 0.5, nearest);
                        if (coverage <= 0) break;
                    }

                    if (coverage <= 0) continue;

                    Blending.Composite(draw.Blend, row.Slice(x * 4, 4), pixel, coverage);
                }
            }
        }

        /// <summary>Coverage from a mask that shares the layer's own grid.</summary>
        private static double SampleCoverage(PixelBuffer mask, double u, double v, bool nearest)
        {
            Span<byte> sample = stackalloc byte[4];
            SampleBuffer(mask, u * mask.Width, v * mask.Height, nearest, sample);
            // A mask is grey with full alpha; outside it there is nothing, which hides.
            return sample[3] == 0 ? 0 : sample[0] / 255.0;
        }

        /// <summary>Coverage from a clip placed on the document.</summary>
        private static double SampleClip(MaskClip clip, double documentX, double documentY, bool nearest)
        {
            LayerTransform placement = clip.Placement;
            double cos = Math.Cos(placement.Radians), sin = Math.Sin(placement.Radians);
            Point center = placement.Center;

            double dx = documentX - center.X, dy = documentY - center.Y;
            double localX = dx * cos + dy * sin;
            double localY = -dx * sin + dy * cos;
            if (placement.FlipX) localX = -localX;
            if (placement.FlipY) localY = -localY;

            // Outside a folder's mask rectangle is hidden, exactly as upstream treats it.
            if (Math.Abs(localX) > placement.Size.Width / 2 || Math.Abs(localY) > placement.Size.Height / 2)
                return 0;

            double u = localX / placement.Size.Width + 0.5;
            double v = localY / placement.Size.Height + 0.5;

            Span<byte> sample = stackalloc byte[4];
            SampleBuffer(clip.Coverage, u * clip.Coverage.Width, v * clip.Coverage.Height, nearest, sample);
            return sample[3] == 0 ? 0 : sample[0] / 255.0;
        }

        public void Dispose() => _pixels.Release();

        /// <summary>
        /// Samples a buffer at pixel coordinates, treating everything outside as transparent.
        /// </summary>
        /// <remarks>
        /// Transparent rather than clamped-to-edge, because a layer's colour must not spread past
        /// its own bounds — upstream pads with transparent pixels for the same reason, so that an
        /// edge fades out the same way wherever the image happens to be cut.
        /// </remarks>
        internal static void SampleBuffer(PixelBuffer buffer, double px, double py, bool nearest, Span<byte> result)
        {
            if (nearest)
            {
                int x = (int)Math.Floor(px), y = (int)Math.Floor(py);
                Read(buffer, x, y, result);
                return;
            }

            double fx = px - 0.5, fy = py - 0.5;
            int x0 = (int)Math.Floor(fx), y0 = (int)Math.Floor(fy);
            double tx = fx - x0, ty = fy - y0;

            Span<byte> a = stackalloc byte[4], b = stackalloc byte[4], c = stackalloc byte[4], d = stackalloc byte[4];
            Read(buffer, x0, y0, a);
            Read(buffer, x0 + 1, y0, b);
            Read(buffer, x0, y0 + 1, c);
            Read(buffer, x0 + 1, y0 + 1, d);

            for (int channel = 0; channel < 4; channel++)
            {
                double top = a[channel] * (1 - tx) + b[channel] * tx;
                double bottom = c[channel] * (1 - tx) + d[channel] * tx;
                result[channel] = (byte)Math.Clamp(
                    Math.Round(top * (1 - ty) + bottom * ty, MidpointRounding.AwayFromZero), 0, 255);
            }

            // Interpolating premultiplied values keeps every channel at or below its alpha, but
            // rounding each one separately can still push one a step over.
            for (int channel = 0; channel < 3; channel++)
                result[channel] = Math.Min(result[channel], result[3]);
        }

        private static void Read(PixelBuffer buffer, int x, int y, Span<byte> result)
        {
            if (x < 0 || y < 0 || x >= buffer.Width || y >= buffer.Height)
            {
                result.Clear();
                return;
            }

            buffer.Row(y).Slice(x * 4, 4).CopyTo(result);
        }
    }

    /// <summary>
    /// The pixels a draw reads from: the region it actually touches, reduced to the level it needs.
    /// </summary>
    /// <remarks>
    /// Materialising only the touched region is what keeps tile replacement worth having — a stroke
    /// on a large layer reads the tiles it changed, not the layer. The region is padded by one
    /// reduced pixel so the final bilinear sample has its neighbours, and snapped outwards to a
    /// multiple of the halving so the reduction lines up with the whole image's.
    /// </remarks>
    private sealed class SampledSource : IDisposable
    {
        private readonly PixelBuffer _pixels;
        private readonly bool _owned;
        private readonly int _level;
        private readonly int _offsetX;
        private readonly int _offsetY;
        private readonly int _sourceWidth;
        private readonly int _sourceHeight;

        public SampledSource(IPixelSource source, DownsamplePyramid pyramid, int level,
                             PixelRect area, LayerTransform placement)
        {
            _sourceWidth = source.Width;
            _sourceHeight = source.Height;

            PixelRect needed = LayerGeometry.SourceRegion(area, placement, source.Width, source.Height);
            needed = LayerGeometry.Snap(needed, 1 << level, source.Width, source.Height);

            bool whole = needed.X == 0 && needed.Y == 0
                         && needed.Width == source.Width && needed.Height == source.Height;

            if (whole && source is BufferSource plain)
            {
                // The common case: a layer drawn whole, so the cached pyramid does the work.
                (PixelBuffer reduced, int applied) = pyramid.Reduced(plain.Buffer, level);
                _pixels = reduced;
                _owned = false;
                _level = applied;
                _offsetX = 0;
                _offsetY = 0;
                return;
            }

            PixelBuffer region = source.Materialize(needed);
            int reducedBy = 0;
            for (int i = 0; i < level && (region.Width > 1 || region.Height > 1); i++)
            {
                PixelBuffer next = DownsamplePyramid.Halve(region);
                region.Release();
                region = next;
                reducedBy++;
            }

            _pixels = region;
            _owned = true;
            _level = reducedBy;
            _offsetX = needed.X;
            _offsetY = needed.Y;
        }

        /// <summary>Samples at unit coordinates over the whole layer.</summary>
        public void Sample(double u, double v, bool nearest, Span<byte> result)
        {
            double step = 1 << _level;
            double px = (u * _sourceWidth - _offsetX) / step;
            double py = (v * _sourceHeight - _offsetY) / step;
            Surface.SampleBuffer(_pixels, px, py, nearest, result);
        }

        public void Dispose()
        {
            if (_owned) _pixels.Release();
        }

    }
}
