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

    /// <summary>Draws <paramref name="document"/> onto an existing surface.</summary>
    public static void Draw(CanvasDocument document, IRenderSurface surface, IRenderBackend backend)
    {
        List<Item> items = RenderOrder(document);
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

                using PixelBuffer coverage = Coverage(source, document, backend);
                DrawOne(item, surface, new MaskClip(coverage, canvas));
                continue;
            }

            List<Item> clipped = ContiguousClipped(items, i, item.Layer.Id);
            if (clipped.Count == 0)
            {
                DrawOne(item, surface, extra: null);
                continue;
            }

            DrawClippingGroup(item, clipped, surface, backend);
            for (int k = 1; k <= clipped.Count; k++) consumed.Add(i + k);
        }
    }

    /// <summary>
    /// The layers to draw, bottom to top, each with the folder masks that apply to it.
    /// </summary>
    private static List<Item> RenderOrder(CanvasDocument document)
    {
        var children = new Dictionary<Guid?, List<ImageLayer>>();
        foreach (ImageLayer layer in document.Layers)
        {
            if (!children.TryGetValue(layer.ParentId, out List<ImageLayer>? siblings))
                children[layer.ParentId] = siblings = [];
            siblings.Add(layer);
        }

        var result = new List<Item>();
        Visit(null, visible: true, clips: [], depth: 0);
        return result;

        void Visit(Guid? parent, bool visible, IReadOnlyList<MaskClip> clips, int depth)
        {
            if (depth > ProjectLimits.MaximumNesting) return;
            if (!children.TryGetValue(parent, out List<ImageLayer>? siblings)) return;

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

                if (!effective || layer.Image is null) continue;
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
    private static void DrawClippingGroup(Item baseItem, List<Item> clipped,
                                          IRenderSurface surface, IRenderBackend backend)
    {
        using IRenderSurface group = backend.CreateSurface(surface.Width, surface.Height);
        group.Clear();

        // The base goes down with its own opacity but in Normal: what it would blend against is
        // outside this surface, and the group as a whole carries its blend mode instead.
        DrawOne(baseItem, group, extra: null, blend: LayerBlendMode.Normal);

        using PixelBuffer basePixels = group.Read();
        using ClippingGroup.Coverage coverage = ClippingGroup.ExtractAlpha(basePixels);
        ClippingGroup.MakeOpaque(basePixels);
        group.Write(basePixels);

        // No clip here on purpose: the layers composite against an opaque backdrop at full
        // strength, and the base's coverage is reapplied to the result afterwards.
        foreach (Item item in clipped) DrawOne(item, group, extra: null);

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

    private static void DrawOne(Item item, IRenderSurface surface, MaskClip? extra,
                                LayerBlendMode? blend = null)
    {
        ImageLayer layer = item.Layer;
        if (layer.Image is not PixelBuffer image) return;

        IReadOnlyList<MaskClip> clips = extra is MaskClip clip ? [.. item.Clips, clip] : item.Clips;

        surface.Draw(new LayerDraw
        {
            Source = new BufferSource(image),
            Placement = layer.Transform,
            Opacity = layer.Opacity,
            Blend = blend ?? layer.BlendMode,
            Mask = layer.Mask is { IsEnabled: true } mask && mask.Placement is null ? mask.Coverage : null,
            Clips = layer.Mask is { IsEnabled: true, Placement: LayerTransform placement } placed
                ? [.. clips, new MaskClip(placed.Coverage, placement)]
                : clips,
        });
    }

    /// <summary>What one layer covers, on its own, as a document-sized grey buffer.</summary>
    private static PixelBuffer Coverage(ImageLayer layer, CanvasDocument document, IRenderBackend backend)
    {
        using IRenderSurface surface = backend.CreateSurface(document.Width, document.Height);
        surface.Clear();

        // Coverage ignores visibility and colour: a hidden layer still clips, and only its alpha
        // — including its own mask and opacity — decides what shows through.
        DrawOne(new Item(layer with { IsVisible = true }, []), surface, extra: null,
                blend: LayerBlendMode.Normal);

        using PixelBuffer pixels = surface.Read();
        using ClippingGroup.Coverage alpha = ClippingGroup.ExtractAlpha(pixels);
        return ClippingGroup.AsMask(alpha);
    }
}
