using System.Numerics;
using Compositor_korean_win.Core;
using SharpGen.Runtime;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;
using AlphaMode = Vortice.DCommon.AlphaMode;
using BlendEffect = Vortice.Direct2D1.Effects.Blend;
using BlendMode = Vortice.Direct2D1.BlendMode;
// Vortice has a Rect of its own, and it is not the one the render interface speaks in.
using Rect = Compositor_korean_win.Core.Rect;

namespace Compositor_korean_win.Shell;

/// <summary>
/// The renderer that ships: Direct2D, on whatever GPU is there.
/// </summary>
/// <remarks>
/// <para>
/// This is the implementation docs/windows-port.md §3 maps out — the same compositing the software
/// backend does, handed to the hardware. Everything it needs is in Windows: no renderer is bundled,
/// which is the whole argument for the boundary in §4.2.
/// </para>
/// <para>
/// <b>How a layer gets drawn.</b> Direct2D's primitive blending only covers source-over, so a layer
/// in any other mode goes through the Blend effect, which takes the pixels underneath and the
/// layer as two inputs. That means a layer is built on an intermediate surface first, its masks are
/// multiplied in there, and only then is it blended onto the target. A Normal layer skips all of
/// that and draws straight on.
/// </para>
/// <para>
/// <b>Coverage carries in alpha here.</b> A Core mask keeps its coverage in the colour channels
/// with alpha full — that is what the format stores. The Alpha Mask effect reads the alpha instead,
/// so masks are turned round on the way to the GPU.
/// </para>
/// </remarks>
internal sealed class Direct2DBackend(GraphicsDevice device) : IRenderBackend
{
    /// <summary>
    /// Offscreen surfaces are always RGBA, whatever the swap chain ended up as.
    /// </summary>
    /// <remarks>
    /// The C kernels assume RGBA and the Core layer's buffers are RGBA, so keeping the compositing
    /// surfaces in that order means no channel swap anywhere. M0 measured that the format is
    /// supported as a render target; only the swap chain is ever restricted.
    /// </remarks>
    private const Vortice.DXGI.Format SurfaceFormat = Vortice.DXGI.Format.R8G8B8A8_UNorm;

    private readonly DownsamplePyramid _pyramid = new();
    private readonly UploadCache _uploads = new();

    public string Name => device.IsWarp ? "direct2d (warp)" : "direct2d";

    public IRenderSurface CreateSurface(int width, int height) =>
        new Surface(device, width, height, _pyramid, _uploads);

    /// <summary>
    /// A surface that draws straight onto the window's back buffer.
    /// </summary>
    /// <remarks>
    /// The canvas composites a frame here rather than offscreen. Both would cost the same to draw;
    /// the difference is that an offscreen frame then has to reach the window, and the only way
    /// back from a Direct2D target the GPU owns is a CPU copy — a window's worth of pixels down and
    /// the same back up, every frame, for nothing.
    /// </remarks>
    public IRenderSurface CreateWindowSurface(int width, int height)
    {
        ID2D1Bitmap1 target = device.BackBuffer
            ?? throw new InvalidOperationException("no window has been bound");

        return new Surface(device, target, width, height, _pyramid, _uploads);
    }

    public PixelBuffer Downsample(PixelBuffer source, int level)
    {
        (PixelBuffer image, int _) = _pyramid.Reduced(source, level);
        return PixelRegion.Copy(image, new PixelRect(0, 0, image.Width, image.Height));
    }

    public void Dispose()
    {
        _uploads.Dispose();
        _pyramid.Dispose();
    }

    /// <summary>The blend modes, mapped onto Direct2D's effect. Normal never gets here.</summary>
    private static BlendMode ToBlendMode(LayerBlendMode mode) => mode switch
    {
        LayerBlendMode.Multiply => BlendMode.Multiply,
        LayerBlendMode.Screen => BlendMode.Screen,
        LayerBlendMode.Overlay => BlendMode.Overlay,
        LayerBlendMode.Darken => BlendMode.Darken,
        LayerBlendMode.Lighten => BlendMode.Lighten,
        LayerBlendMode.Difference => BlendMode.Difference,
        LayerBlendMode.ColorDodge => BlendMode.ColorDodge,
        LayerBlendMode.ColorBurn => BlendMode.ColorBurn,
        LayerBlendMode.Hue => BlendMode.Hue,
        LayerBlendMode.Saturation => BlendMode.Saturation,
        LayerBlendMode.Color => BlendMode.Color,
        LayerBlendMode.Luminosity => BlendMode.Luminosity,
        _ => BlendMode.Multiply,
    };

    private sealed class Surface : IRenderSurface
    {
        private readonly GraphicsDevice _device;
        private readonly ID2D1Bitmap1 _target;
        private readonly DownsamplePyramid _pyramid;
        private readonly UploadCache _uploads;
        private readonly bool _ownsTarget;
        private readonly Stack<PixelRect> _clips = new();
        private ID2D1Bitmap1? _staging;

        public Surface(GraphicsDevice device, int width, int height,
                       DownsamplePyramid pyramid, UploadCache uploads)
            : this(device, CreateBitmap(device.D2DContext, width, height, BitmapOptions.Target),
                   width, height, pyramid, uploads)
        {
            _ownsTarget = true;
        }

        /// <summary>Draws onto a target someone else owns — the window's back buffer.</summary>
        public Surface(GraphicsDevice device, ID2D1Bitmap1 target, int width, int height,
                       DownsamplePyramid pyramid, UploadCache uploads)
        {
            _device = device;
            _target = target;
            _pyramid = pyramid;
            _uploads = uploads;
            Width = width;
            Height = height;
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>What draws may touch: the innermost clip, or nothing at all.</summary>
        private PixelRect? Clip => _clips.Count > 0 ? _clips.Peek() : null;

        public void PushClip(Rect region) =>
            _clips.Push(region.Rounded().Intersect(Clip ?? new PixelRect(0, 0, Width, Height)));

        public void PopClip()
        {
            if (_clips.Count > 0) _clips.Pop();
        }

        private static ID2D1Bitmap1 CreateBitmap(ID2D1DeviceContext context, int width, int height,
                                                 BitmapOptions options)
        {
            var properties = new BitmapProperties1
            {
                BitmapOptions = options,
                PixelFormat = new Vortice.DCommon.PixelFormat(SurfaceFormat, AlphaMode.Premultiplied),
            };

            return context.CreateBitmap(new SizeI(width, height), properties);
        }

        public void Clear()
        {
            ID2D1DeviceContext context = _device.D2DContext;
            using var _ = new TargetScope(context, _target);
            context.Clear(new Color4(0f, 0f, 0f, 0f));
        }

        public void Draw(LayerDraw draw)
        {
            if (!draw.Placement.IsDrawable || draw.Opacity <= 0) return;

            // Where the layer lands, and which of its own pixels feed that. Handing Direct2D the
            // whole layer and letting it resample would upload four hundred megabytes a frame for
            // a hundred-megapixel layer; this uploads the window's worth of pixels instead.
            PixelRect area = LayerGeometry.Bounds(draw.Placement)
                .Intersect(Clip ?? new PixelRect(0, 0, Width, Height));
            if (area.IsEmpty) return;

            int level = LayerGeometry.LevelFor(draw.Placement, draw.Source.Width);
            PixelRect needed = LayerGeometry.Snap(
                LayerGeometry.SourceRegion(area, draw.Placement, draw.Source.Width, draw.Source.Height),
                1 << level, draw.Source.Width, draw.Source.Height);

            using Piece piece = Resolve(draw, needed, level);
            ID2D1Bitmap1 source = piece.Bitmap;
            draw = draw with
            {
                Placement = LayerGeometry.Place(draw.Placement, needed,
                                                draw.Source.Width, draw.Source.Height),
            };

            bool plain = draw.Blend == LayerBlendMode.Normal && draw.Clips.Count == 0;
            if (plain)
            {
                using var _ = new TargetScope(_device.D2DContext, _target, Clip);
                DrawPlaced(_device.D2DContext, source, draw, piece.Width, piece.Height);
                return;
            }

            // Anything else is built on its own surface first: masks in document space have to be
            // applied after the placement, and a blend mode needs the layer as a whole image.
            using ID2D1Bitmap1 layer = CreateBitmap(_device.D2DContext, Width, Height, BitmapOptions.Target);
            using (var _ = new TargetScope(_device.D2DContext, layer))
            {
                _device.D2DContext.Clear(new Color4(0f, 0f, 0f, 0f));
                DrawPlaced(_device.D2DContext, source, draw, piece.Width, piece.Height);
            }

            ID2D1Image composed = layer;
            var masks = new List<IDisposable>();

            try
            {
                foreach (MaskClip clip in draw.Clips)
                {
                    ID2D1Bitmap1 coverage = RenderClip(clip);
                    masks.Add(coverage);

                    var effect = new Vortice.Direct2D1.Effects.AlphaMask(_device.D2DContext);
                    effect.SetInput(0, composed, true);
                    effect.SetInput(1, coverage, true);
                    masks.Add(effect);
                    composed = effect.Output;
                    masks.Add(composed);
                }

                if (draw.Blend == LayerBlendMode.Normal)
                {
                    using var _ = new TargetScope(_device.D2DContext, _target, Clip);
                    _device.D2DContext.DrawImage(composed, InterpolationMode.NearestNeighbor,
                                                 CompositeMode.SourceOver);
                    return;
                }

                // Blend takes what is already on the target as its first input. Direct2D will not
                // read a target while it is bound, so the copy has to happen before the scope opens.
                // The copy must match the target exactly. The window's back buffer is a swap-chain
                // surface whose alpha mode is Ignore, not Premultiplied, and CopyFromBitmap rejects
                // any format or size mismatch with E_INVALIDARG — so a blended layer on the live
                // canvas crashed while the same layer rendered fine offscreen.
                using ID2D1Bitmap1 backdrop = _device.D2DContext.CreateBitmap(_target.PixelSize,
                    new BitmapProperties1
                    {
                        BitmapOptions = BitmapOptions.None,
                        PixelFormat = _target.PixelFormat,
                    });
                backdrop.CopyFromBitmap(_target).CheckError();

                using var blend = new BlendEffect(_device.D2DContext) { Mode = ToBlendMode(draw.Blend) };
                blend.SetInput(0, backdrop, true);
                blend.SetInput(1, composed, true);

                // SourceCopy replaces what it covers, so the clip is doing real work here: without
                // it a blended layer would wipe the rest of the frame rather than blend onto it.
                using var scope = new TargetScope(_device.D2DContext, _target, Clip);
                _device.D2DContext.DrawImage(blend, InterpolationMode.NearestNeighbor, CompositeMode.SourceCopy);
            }
            finally
            {
                for (int i = masks.Count - 1; i >= 0; i--) masks[i].Dispose();
            }
        }

        /// <summary>Places the source with the layer's transform and draws it once.</summary>
        private static void DrawPlaced(ID2D1DeviceContext context, ID2D1Bitmap1 source, LayerDraw draw,
                                       int sourceWidth, int sourceHeight)
        {
            LayerTransform placement = draw.Placement;

            float scaleX = (float)(placement.Size.Width / Math.Max(1, sourceWidth)) * (placement.FlipX ? -1 : 1);
            float scaleY = (float)(placement.Size.Height / Math.Max(1, sourceHeight)) * (placement.FlipY ? -1 : 1);

            // Row-vector composition, so this reads in the order it applies: centre the pixels,
            // scale and flip, turn, then move onto the document.
            Matrix3x2 transform =
                Matrix3x2.CreateTranslation(-sourceWidth / 2f, -sourceHeight / 2f)
                * Matrix3x2.CreateScale(scaleX, scaleY)
                * Matrix3x2.CreateRotation((float)placement.Radians)
                * Matrix3x2.CreateTranslation((float)placement.Center.X, (float)placement.Center.Y);

            context.Transform = transform;
            context.DrawBitmap(source, (float)draw.Opacity,
                               placement.Sampling == LayerSampling.Nearest
                                   ? InterpolationMode.NearestNeighbor
                                   : InterpolationMode.HighQualityCubic);
            context.Transform = Matrix3x2.Identity;
        }

        /// <summary>
        /// A document-sized bitmap whose alpha is the clip's coverage, and nothing outside it.
        /// </summary>
        private ID2D1Bitmap1 RenderClip(MaskClip clip)
        {
            using PixelBuffer asAlpha = CoverageAsAlpha(clip.Coverage);
            using ID2D1Bitmap1 source = Upload(_device.D2DContext, asAlpha);

            ID2D1Bitmap1 coverage = CreateBitmap(_device.D2DContext, Width, Height, BitmapOptions.Target);
            using (var _ = new TargetScope(_device.D2DContext, coverage))
            {
                // Cleared to what lies past the clip's rectangle — nothing for a folder's mask, the
                // edge's level for a mask placed apart — the same rule the software backend follows.
                // The mask then replaces that rather than laying over it, or a grey inside the
                // rectangle would come out lighter than it is.
                _device.D2DContext.Clear(new Color4(0f, 0f, 0f, clip.Outside / 255f));
                PrimitiveBlend blend = _device.D2DContext.PrimitiveBlend;
                _device.D2DContext.PrimitiveBlend = PrimitiveBlend.Copy;
                DrawPlaced(_device.D2DContext, source,
                           new LayerDraw { Source = new BufferSource(asAlpha), Placement = clip.Placement },
                           asAlpha.Width, asAlpha.Height);
                _device.D2DContext.PrimitiveBlend = blend;
            }

            return coverage;
        }

        public PixelBuffer Read()
        {
            // Made on demand: the window's frames are never read back, and a staging bitmap for
            // every surface would be a second copy of each one for nothing.
            _staging ??= CreateBitmap(_device.D2DContext, Width, Height,
                                      BitmapOptions.CpuRead | BitmapOptions.CannotDraw);

            _staging.CopyFromBitmap(_target).CheckError();

            MappedRectangle mapped = _staging.Map(MapOptions.Read);
            try
            {
                PixelBuffer result = PixelBuffer.Allocate(Width, Height);
                unsafe
                {
                    for (int y = 0; y < Height; y++)
                    {
                        var row = new ReadOnlySpan<byte>((byte*)mapped.Bits + (long)y * mapped.Pitch, Width * 4);
                        row.CopyTo(result.Row(y));
                    }
                }
                return result;
            }
            finally
            {
                _staging.Unmap();
            }
        }

        public void Write(PixelBuffer pixels)
        {
            if (pixels.Width != Width || pixels.Height != Height)
                throw new ArgumentException("not the surface's size", nameof(pixels));

            using ID2D1Bitmap1 source = Upload(_device.D2DContext, pixels);
            using var _ = new TargetScope(_device.D2DContext, _target);
            _device.D2DContext.Clear(new Color4(0f, 0f, 0f, 0f));
            _device.D2DContext.DrawBitmap(source, 1f, InterpolationMode.NearestNeighbor);
        }

        public void Dispose()
        {
            _staging?.Dispose();
            if (_ownsTarget) _target.Dispose();
        }

        /// <summary>One region of a layer's pixels, uploaded and ready to draw.</summary>
        /// <remarks>
        /// A cached piece belongs to the backend and outlives the draw; a piece built for this draw
        /// alone — a masked layer, or a layer held as tiles — is released with it.
        /// </remarks>
        private readonly struct Piece(ID2D1Bitmap1 bitmap, int width, int height, bool owned) : IDisposable
        {
            public ID2D1Bitmap1 Bitmap { get; } = bitmap;
            public int Width { get; } = width;
            public int Height { get; } = height;

            public void Dispose()
            {
                if (owned) Bitmap.Dispose();
            }
        }

        /// <summary>
        /// The pixels for one draw: the region it needs, reduced to the level it needs, on the GPU.
        /// </summary>
        /// <remarks>
        /// A plain layer with no mask is cached by buffer, level and region, so a frame that has
        /// not moved re-uses what the last one uploaded. Panning changes the region and pays for a
        /// new upload, which is a window's worth of pixels and not a document's.
        /// </remarks>
        private Piece Resolve(LayerDraw draw, PixelRect needed, int level)
        {
            bool cacheable = draw.Mask is null && draw.Source is BufferSource { Cacheable: true };
            UploadCache.Key key = default;

            if (cacheable)
            {
                key = new UploadCache.Key(((BufferSource)draw.Source).Buffer, level, needed);
                if (_uploads.TryGet(key, out ID2D1Bitmap1? cached, out int cachedWidth, out int cachedHeight))
                    return new Piece(cached, cachedWidth, cachedHeight, owned: false);
            }

            PixelBuffer pixels = Reduce(draw.Source, needed, level);
            try
            {
                if (draw.Mask is PixelBuffer mask)
                    ApplyMask(pixels, mask, needed, draw.Source.Width, draw.Source.Height);

                ID2D1Bitmap1 bitmap = Upload(_device.D2DContext, pixels);
                if (!cacheable) return new Piece(bitmap, pixels.Width, pixels.Height, owned: true);

                _uploads.Put(key, bitmap, pixels.Width, pixels.Height);
                return new Piece(bitmap, pixels.Width, pixels.Height, owned: false);
            }
            finally
            {
                pixels.Release();
            }
        }

        /// <summary>
        /// <paramref name="needed"/> of a source, halved <paramref name="level"/> times.
        /// </summary>
        /// <remarks>
        /// A layer drawn whole goes through the shared pyramid, which keeps its halvings; anything
        /// else is materialised and halved here. Both give the same pixels, because the box filter
        /// reaches nothing outside its own block and the region is snapped to the halving grid.
        /// </remarks>
        private PixelBuffer Reduce(IPixelSource source, PixelRect needed, int level)
        {
            bool whole = needed.X == 0 && needed.Y == 0
                         && needed.Width == source.Width && needed.Height == source.Height;

            if (whole && source is BufferSource plain)
            {
                (PixelBuffer reduced, int _) = _pyramid.Reduced(plain.Buffer, level);
                return reduced.Retain();
            }

            PixelBuffer region = source.Materialize(needed);
            for (int i = 0; i < level && (region.Width > 1 || region.Height > 1); i++)
            {
                PixelBuffer next = DownsamplePyramid.Halve(region);
                region.Release();
                region = next;
            }

            return region;
        }

        /// <summary>
        /// Multiplies a layer's own mask into one region of its pixels.
        /// </summary>
        /// <remarks>
        /// The mask shares the layer's normalised extent rather than its pixel count — an unpainted
        /// mask is a single pixel covering the whole layer — so the region's own offset has to go
        /// through that normalisation rather than through the mask's dimensions.
        /// </remarks>
        private static void ApplyMask(PixelBuffer pixels, PixelBuffer mask, PixelRect region,
                                      int sourceWidth, int sourceHeight)
        {
            for (int y = 0; y < pixels.Height; y++)
            {
                double v = (region.Y + (y + 0.5) * region.Height / pixels.Height) / sourceHeight;
                Span<byte> coverage = mask.Row(Math.Clamp((int)(v * mask.Height), 0, mask.Height - 1));
                Span<byte> row = pixels.Row(y);

                for (int x = 0; x < pixels.Width; x++)
                {
                    double u = (region.X + (x + 0.5) * region.Width / pixels.Width) / sourceWidth;
                    int level = coverage[Math.Clamp((int)(u * mask.Width), 0, mask.Width - 1) * 4];
                    if (level == 255) continue;

                    for (int channel = 0; channel < 4; channel++)
                        row[x * 4 + channel] = (byte)(row[x * 4 + channel] * level / 255);
                }
            }
        }

        /// <summary>Coverage moved from the colour channels into alpha, where the effect reads it.</summary>
        private static PixelBuffer CoverageAsAlpha(PixelBuffer coverage)
        {
            PixelBuffer result = PixelBuffer.Allocate(coverage.Width, coverage.Height);
            for (int y = 0; y < coverage.Height; y++)
            {
                Span<byte> source = coverage.Row(y);
                Span<byte> target = result.Row(y);
                for (int x = 0; x < coverage.Width; x++)
                {
                    byte level = source[x * 4];
                    target[x * 4 + 0] = level;
                    target[x * 4 + 1] = level;
                    target[x * 4 + 2] = level;
                    target[x * 4 + 3] = level;
                }
            }
            return result;
        }

        private static ID2D1Bitmap1 Upload(ID2D1DeviceContext context, PixelBuffer pixels)
        {
            var properties = new BitmapProperties1
            {
                PixelFormat = new Vortice.DCommon.PixelFormat(SurfaceFormat, AlphaMode.Premultiplied),
            };

            return context.CreateBitmap(new SizeI(pixels.Width, pixels.Height),
                                        pixels.Scan0, (uint)pixels.Stride, properties);
        }

        /// <summary>Binds a target for the length of a draw and puts the old one back.</summary>
        /// <remarks>
        /// A clip belongs to the context and only holds between BeginDraw and EndDraw, so it is
        /// pushed here rather than kept across draws. Aliased, because the software backend rounds
        /// a clip to whole pixels and the two have to land on the same edge.
        /// </remarks>
        private readonly struct TargetScope : IDisposable
        {
            private readonly ID2D1DeviceContext _context;
            private readonly ID2D1Image? _previous;
            private readonly bool _clipped;

            public TargetScope(ID2D1DeviceContext context, ID2D1Bitmap1 target, PixelRect? clip = null)
            {
                _context = context;
                _previous = context.Target;
                context.Target = target;
                context.BeginDraw();

                _clipped = clip is PixelRect region && !region.IsEmpty;
                if (clip is PixelRect rectangle && _clipped)
                {
                    context.PushAxisAlignedClip(
                        new Vortice.RawRectF(rectangle.X, rectangle.Y, rectangle.Right, rectangle.Bottom),
                        AntialiasMode.Aliased);
                }
            }

            public void Dispose()
            {
                if (_clipped) _context.PopAxisAlignedClip();
                _context.EndDraw().CheckError();
                _context.Target = _previous;
                _previous?.Dispose();
            }
        }
    }
}
