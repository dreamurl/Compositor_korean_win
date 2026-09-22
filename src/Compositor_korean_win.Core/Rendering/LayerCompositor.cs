namespace Compositor_korean_win.Core;

/// <summary>
/// Walks a document's layer tree and draws it.
/// </summary>
/// <remarks>
/// <para>
/// Three rules do most of the work, and all three come from upstream:
/// </para>
/// <list type="number">
/// <item><b>Folders are pass-through.</b> A folder is never drawn; its descendants are, in order,
/// and each one carries the masks of every folder it sits inside. Visibility is inherited without
/// changing a child's own flag.</item>
/// <item><b>A clipping mask borrows alpha.</b> A layer with a <c>maskSourceID</c> shows only where
/// the layer it points at is opaque. Colour and visibility of that layer do not matter; only its
/// coverage.</item>
/// <item><b>A clipping group is composited once.</b> When the clipped layers are contiguous
/// siblings sitting directly above their base, the base and its followers go onto a surface of
/// their own, the base's alpha is kept, and the result is composited in one go. Drawing them one
/// after another instead would put two translucent copies of a soft edge over each other and
/// thicken it — upstream's note, and the reason the group surface exists.</item>
/// </list>
/// <para>
/// Adjustment layers draw nothing here. They are read and written by the format layer already, but
/// applying one means running a filter over what is underneath, which is M5.
/// </para>
/// </remarks>
/// <summary>
/// A layer being edited right now, drawn from an edit's own pixels instead of its buffer.
/// </summary>
/// <remarks>
/// This is how a brush stroke reaches the screen before it has been committed: the stroke holds
/// the tiles it has rebuilt and hands them over as a <see cref="LayerRaster"/>, so a stroke on a
/// hundred-megapixel layer draws the tiles it touched rather than rebuilding the layer on every
/// mouse move (docs/windows-port.md §2.3).
/// </remarks>
public sealed record LiveEdit(Guid LayerId, IPixelSource Source);

public static class LayerCompositor
{
    /// <summary>One layer in render order, with everything needed to draw it.</summary>
    private sealed record Item(ImageLayer Layer, IReadOnlyList<MaskClip> Clips);

    /// <summary>Composites <paramref name="document"/> onto a new surface.</summary>
    /// <remarks>The caller owns the returned pixels and releases them.</remarks>
    public static PixelBuffer Render(CanvasDocument document, IRenderBackend backend)
    {
        using IRenderSurface surface = backend.CreateSurface(document.Width, document.Height);
        surface.Clear();
        Draw(document, surface, backend);
        return surface.Read();
    }

    /// <summary>
    /// Composites what <paramref name="viewport"/> shows onto a new surface of the view's size, in
    /// device pixels.
    /// </summary>
    /// <remarks>The caller owns the returned pixels and releases them.</remarks>
    public static PixelBuffer RenderView(CanvasDocument document, CanvasViewport viewport,
                                         IRenderBackend backend)
    {
        Size frame = viewport.DeviceViewSize;
        using IRenderSurface surface = backend.CreateSurface(
            Math.Max(1, (int)Math.Round(frame.Width, MidpointRounding.AwayFromZero)),
            Math.Max(1, (int)Math.Round(frame.Height, MidpointRounding.AwayFromZero)));

        surface.Clear();
        DrawView(document, viewport, surface, backend);
        return surface.Read();
    }

    /// <summary>
    /// Draws what <paramref name="viewport"/> shows onto a surface holding one frame.
    /// </summary>
    /// <remarks>
    /// The same walk as <see cref="Draw(CanvasDocument, IRenderSurface, IRenderBackend)"/>, with
    /// every placement carried into the frame's own pixels, and the canvas clipping what is drawn:
    /// on a surface the size of the document its edges did that for free, and here they do not.
    /// </remarks>
    public static void DrawView(CanvasDocument document, CanvasViewport viewport,
                                IRenderSurface surface, IRenderBackend backend, LiveEdit? live = null)
    {
        CanvasProjection projection = viewport.DeviceProjection(document.Size);

        surface.PushClip(projection.Apply(new Rect(0, 0, document.Width, document.Height)));
        try
        {
            Draw(document, surface, backend, projection, live);
        }
        finally
        {
            surface.PopClip();
        }
    }

    /// <summary>Draws <paramref name="document"/> onto an existing surface.</summary>
    public static void Draw(CanvasDocument document, IRenderSurface surface, IRenderBackend backend) =>
        Draw(document, surface, backend, CanvasProjection.Identity);

    /// <summary>
    /// Draws <paramref name="document"/> onto an existing surface, placed by
    /// <paramref name="projection"/>.
    /// </summary>
    public static void Draw(CanvasDocument document, IRenderSurface surface, IRenderBackend backend,
                            CanvasProjection projection, LiveEdit? live = null)
    {
        List<Item> items = RenderOrder(document, live?.LayerId);
        var byId = document.Layers.ToDictionary(layer => layer.Id);
        var canvas = new LayerTransform(Point.Zero, new Size(surface.Width, surface.Height));

        // Indices a clipping group has already drawn.
        var consumed = new HashSet<int>();

        for (int i = 0; i < items.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            Item item = items[i];

            if (item.Layer.MaskSourceId is Guid sourceId)
            {
                // Reaching here means the base is not the sibling directly below, so there is no
                // group to join. The format still allows it, so the layer draws on its own,
                // restricted to what its base covers.
                if (!byId.TryGetValue(sourceId, out ImageLayer? source)) continue;

                using PixelBuffer coverage = Coverage(source, surface, backend, projection);
                DrawOne(item, surface, projection, new MaskClip(coverage, canvas), live: live);
                continue;
            }

            List<Item> clipped = ContiguousClipped(items, i, item.Layer.Id);
            if (clipped.Count == 0)
            {
                DrawOne(item, surface, projection, extra: null, live: live);
                continue;
            }

            DrawClippingGroup(item, clipped, surface, backend, projection, live);
            for (int k = 1; k <= clipped.Count; k++) consumed.Add(i + k);
        }
    }

    /// <summary>
    /// The layers to draw, bottom to top, each with the folder masks that apply to it.
    /// </summary>
    private static List<Item> RenderOrder(CanvasDocument document, Guid? live = null)
    {
        // Roots are kept apart rather than under a null key, which a dictionary will not take.
        var roots = new List<ImageLayer>();
        var children = new Dictionary<Guid, List<ImageLayer>>();

        foreach (ImageLayer layer in document.Layers)
        {
            if (layer.ParentId is not Guid parent) { roots.Add(layer); continue; }
            if (!children.TryGetValue(parent, out List<ImageLayer>? siblings))
                children[parent] = siblings = [];
            siblings.Add(layer);
        }

        var result = new List<Item>();
        Visit(null, visible: true, clips: [], depth: 0);
        return result;

        void Visit(Guid? parent, bool visible, IReadOnlyList<MaskClip> clips, int depth)
        {
            if (depth > ProjectLimits.MaximumNesting) return;

            List<ImageLayer> siblings;
            if (parent is Guid id)
            {
                if (!children.TryGetValue(id, out List<ImageLayer>? found)) return;
                siblings = found;
            }
            else
            {
                siblings = roots;
            }

            foreach (ImageLayer layer in siblings)
            {
                bool effective = visible && layer.IsVisible;

                if (layer.IsGroup)
                {
                    // A folder's own mask joins the clips its descendants carry. Its rectangle is
                    // the folder's transform, so everything outside it is hidden.
                    IReadOnlyList<MaskClip> inner = clips;
                    if (layer.Mask is { IsEnabled: true } mask)
                    {
                        inner = [.. clips, new MaskClip(mask.Coverage, mask.Placement ?? layer.Transform)];
                    }

                    Visit(layer.Id, effective, inner, depth + 1);
                    continue;
                }

                // A blank layer has no pixels until a stroke is committed, so one being painted
                // on right now has to reach the list anyway.
                if (!effective || (layer.Image is null && layer.Id != live)) continue;
                result.Add(new Item(layer, clips));
            }
        }
    }

    /// <summary>The run of layers directly above <paramref name="index"/> that clip to it.</summary>
    private static List<Item> ContiguousClipped(List<Item> items, int index, Guid baseId)
    {
        var result = new List<Item>();
        for (int i = index + 1; i < items.Count; i++)
        {
            if (items[i].Layer.MaskSourceId != baseId) break;
            if (items[i].Layer.ParentId != items[index].Layer.ParentId) break;
            result.Add(items[i]);
        }
        return result;
    }

    /// <summary>
    /// Draws a base and the layers clipped to it as one unit, keeping the base's own coverage.
    /// </summary>
    /// <remarks>See <see cref="ClippingGroup"/> for why it goes in this order.</remarks>
    private static void DrawClippingGroup(Item baseItem, List<Item> clipped, IRenderSurface surface,
                                          IRenderBackend backend, CanvasProjection projection,
                                          LiveEdit? live = null)
    {
        using IRenderSurface group = backend.CreateSurface(surface.Width, surface.Height);
        group.Clear();

        // The base goes down with its own opacity but in Normal: what it would blend against is
        // outside this surface, and the group as a whole carries its blend mode instead.
        DrawOne(baseItem, group, projection, extra: null, blend: LayerBlendMode.Normal, live: live);

        using PixelBuffer basePixels = group.Read();
        using ClippingGroup.Coverage coverage = ClippingGroup.ExtractAlpha(basePixels);
        ClippingGroup.MakeOpaque(basePixels);
        group.Write(basePixels);

        // No clip here on purpose: the layers composite against an opaque backdrop at full
        // strength, and the base's coverage is reapplied to the result afterwards.
        foreach (Item item in clipped) DrawOne(item, group, projection, extra: null, live: live);

        using PixelBuffer composed = group.Read();
        ClippingGroup.RestoreAlpha(composed, coverage);

        surface.Draw(new LayerDraw
        {
            Source = new BufferSource(composed),
            Placement = new LayerTransform(Point.Zero, new Size(surface.Width, surface.Height))
            {
                Sampling = LayerSampling.Nearest,
            },
            Blend = baseItem.Layer.BlendMode,
        });
    }

    /// <remarks>
    /// <paramref name="projection"/> applies to everything placed on the document — the layer, an
    /// unlinked mask, the folder masks it inherited. It does not apply to <paramref name="extra"/>,
    /// which the caller has already built in the surface's own pixels.
    /// </remarks>
    private static void DrawOne(Item item, IRenderSurface surface, CanvasProjection projection,
                                MaskClip? extra, LayerBlendMode? blend = null, LiveEdit? live = null)
    {
        ImageLayer layer = item.Layer;

        // An edit in progress stands in for the layer's own pixels — including on a blank layer,
        // which has none until the first stroke is committed.
        IPixelSource? source = live is LiveEdit edit && edit.LayerId == layer.Id ? edit.Source : null;
        if (source is null)
        {
            if (layer.Image is not PixelBuffer image) return;
            source = new BufferSource(image);
        }

        var clips = new List<MaskClip>(item.Clips.Count + 2);
        foreach (MaskClip inherited in item.Clips) clips.Add(projection.Apply(inherited));

        if (layer.Mask is { IsEnabled: true, Placement: LayerTransform placement } placed)
            clips.Add(projection.Apply(new MaskClip(placed.Coverage, placement)));

        if (extra is MaskClip clip) clips.Add(clip);

        surface.Draw(new LayerDraw
        {
            Source = source,
            Placement = projection.Apply(layer.Transform),
            Opacity = layer.Opacity,
            Blend = blend ?? layer.BlendMode,
            Mask = layer.Mask is { IsEnabled: true } mask && mask.Placement is null ? mask.Coverage : null,
            Clips = clips,
        });
    }

    /// <summary>What one layer covers, on its own, as a grey buffer the size of the target.</summary>
    private static PixelBuffer Coverage(ImageLayer layer, IRenderSurface target,
                                        IRenderBackend backend, CanvasProjection projection)
    {
        using IRenderSurface surface = backend.CreateSurface(target.Width, target.Height);
        surface.Clear();

        // Coverage ignores visibility and colour: a hidden layer still clips, and only its alpha
        // — including its own mask and opacity — decides what shows through.
        DrawOne(new Item(layer with { IsVisible = true }, []), surface, projection, extra: null,
                blend: LayerBlendMode.Normal);

        using PixelBuffer pixels = surface.Read();
        using ClippingGroup.Coverage alpha = ClippingGroup.ExtractAlpha(pixels);
        return ClippingGroup.AsMask(alpha);
    }
}
