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
/// An adjustment layer draws nothing of its own. It reads back what has been composited beneath
/// it, runs its filter over that and writes the result in its place — through its opacity, its
/// mask and whatever folders it sits in, which all reduce to one weight per pixel. That is why a
/// document with adjustments is composited on a surface of its own first: the target might be the
/// window, with the desk and the sheet already on it, and an adjustment must not tint those.
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
public sealed record LiveEdit(Guid LayerId, IPixelSource Source)
{
    /// <summary>
    /// Where <see cref="Source"/> goes on the document, when it is not where the layer is.
    /// </summary>
    /// <remarks>
    /// A filter preview hands over only the part of the layer in view, reduced, and padded by
    /// however far a blur spreads — a different grid over a different box. The layer's own mask
    /// then no longer shares the source's grid, so it is placed on the document instead, over the
    /// layer's own box, which covers the same pixels.
    /// </remarks>
    public LayerTransform? Placement { get; init; }
}

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

        Rect canvas = projection.Apply(new Rect(0, 0, document.Width, document.Height));
        surface.PushClip(canvas);
        try
        {
            Draw(document, surface, backend, projection, live, canvas);
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
                            CanvasProjection projection, LiveEdit? live = null) =>
        Draw(document, surface, backend, projection, live, clip: null);

    /// <param name="clip">
    /// What the target is clipped to, so a frame composited apart from it can be clipped the same.
    /// </param>
    private static void Draw(CanvasDocument document, IRenderSurface surface, IRenderBackend backend,
                             CanvasProjection projection, LiveEdit? live, Rect? clip)
    {
        List<Item> items = RenderOrder(document, live?.LayerId);

        if (!items.Any(item => IsAdjustment(item.Layer)))
        {
            DrawItems(document, items, surface, backend, projection, live);
            return;
        }

        // Adjustments read back what is under them, and what is under them has to be the layers
        // alone: the target may already hold the window's desk and the white sheet, which are not
        // part of the picture and must not be adjusted with it.
        using IRenderSurface frame = backend.CreateSurface(surface.Width, surface.Height);
        frame.Clear();
        if (clip is Rect region) frame.PushClip(region);

        DrawItems(document, items, frame, backend, projection, live);

        using PixelBuffer composed = frame.Read();
        surface.Draw(new LayerDraw
        {
            Source = new BufferSource(composed) { Cacheable = false },
            Placement = Whole(surface),
        });
    }

    private static void DrawItems(CanvasDocument document, List<Item> items, IRenderSurface surface,
                                  IRenderBackend backend, CanvasProjection projection, LiveEdit? live)
    {
        var byId = document.Layers.ToDictionary(layer => layer.Id);
        var canvas = new LayerTransform(Point.Zero, new Size(surface.Width, surface.Height));

        // Indices a clipping group has already drawn.
        var consumed = new HashSet<int>();

        for (int i = 0; i < items.Count; i++)
        {
            if (consumed.Contains(i)) continue;
            Item item = items[i];

            if (IsAdjustment(item.Layer))
            {
                // Clipped but not part of a group: the adjustment keeps to what its base covers.
                // Upstream leaves such a layer out altogether; restricting it is the reading that
                // agrees with how an ordinary layer in the same place is drawn.
                if (item.Layer.MaskSourceId is Guid adjustedBase)
                {
                    if (!byId.TryGetValue(adjustedBase, out ImageLayer? baseLayer)) continue;
                    using PixelBuffer baseCoverage = Coverage(baseLayer, surface, backend, projection);
                    ApplyAdjustments([(item, new MaskClip(baseCoverage, canvas))], surface, backend, projection);
                    continue;
                }

                // A run of adjustments one over another reads the frame once and writes it once:
                // nothing between them draws, so the surface would only be handed straight back.
                var run = new List<(Item, MaskClip?)>();
                int next = i;
                while (next < items.Count && IsAdjustment(items[next].Layer) && items[next].Layer.MaskSourceId is null)
                {
                    run.Add((items[next], null));
                    next++;
                }

                ApplyAdjustments(run, surface, backend, projection);
                i = next - 1;
                continue;
            }

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
                // An adjustment layer has no pixels either, and is drawn by what it does to the
                // pixels beneath it.
                if (!effective || (layer.Image is null && layer.Id != live && !IsAdjustment(layer))) continue;
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
        foreach (Item item in clipped)
        {
            // An adjustment in a clipping group adjusts the group so far — over the opaque base,
            // so its alpha coming back afterwards is what keeps it inside the base's shape.
            if (IsAdjustment(item.Layer)) ApplyAdjustments([(item, null)], group, backend, projection);
            else DrawOne(item, group, projection, extra: null, live: live);
        }

        using PixelBuffer composed = group.Read();
        ClippingGroup.RestoreAlpha(composed, coverage);

        surface.Draw(new LayerDraw
        {
            Source = new BufferSource(composed) { Cacheable = false },
            Placement = Whole(surface),
            Blend = baseItem.Layer.BlendMode,
        });
    }

    private static bool IsAdjustment(ImageLayer layer) => layer.Adjustment is not null && !layer.IsGroup;

    /// <summary>A placement covering a surface pixel for pixel.</summary>
    private static LayerTransform Whole(IRenderSurface surface) =>
        new(Point.Zero, new Size(surface.Width, surface.Height)) { Sampling = LayerSampling.Nearest };

    /// <summary>
    /// Runs adjustment layers, bottom first, over what is on <paramref name="surface"/> so far.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order for each is upstream's: adjust the whole frame, blend the adjusted frame with the
    /// original if the layer has a blend mode, then let opacity and masks decide how much of that
    /// replaces what was there. A blend mode applies at full coverage with the original alpha put
    /// back afterwards, for the reason a clipping group does the same — blending two translucent
    /// copies of a soft edge would thicken it.
    /// </para>
    /// <para>
    /// The cost is one read and one write of the surface for the whole run, and the surface is the
    /// window's size and not the document's: by the time an adjustment runs, the frame has already
    /// been reduced to what is on screen.
    /// </para>
    /// </remarks>
    /// <param name="run">Each layer with a clip of its own to add, in the surface's pixels.</param>
    private static void ApplyAdjustments(IReadOnlyList<(Item Item, MaskClip? Extra)> run, IRenderSurface surface,
                                         IRenderBackend backend, CanvasProjection projection)
    {
        if (!run.Any(entry => Changes(entry.Item.Layer))) return;

        PixelBuffer current = surface.Read();
        try
        {
            foreach ((Item item, MaskClip? extra) in run)
            {
                if (!Changes(item.Layer)) continue;

                PixelBuffer adjusted = Adjusted(item, current, extra, surface, backend, projection);
                current.Release();
                current = adjusted;
            }

            surface.Write(current);
        }
        finally
        {
            current.Release();
        }

        static bool Changes(ImageLayer layer) =>
            layer.Opacity > 0
            && (layer.BlendMode != LayerBlendMode.Normal || !AdjustmentRendering.IsIdentity(layer.Adjustment!));
    }

    /// <summary>One adjustment layer over <paramref name="original"/>, as a new frame.</summary>
    private static PixelBuffer Adjusted(Item item, PixelBuffer original, MaskClip? extra, IRenderSurface surface,
                                        IRenderBackend backend, CanvasProjection projection)
    {
        ImageLayer layer = item.Layer;

        var clips = new List<MaskClip>(item.Clips.Count + 2);
        foreach (MaskClip inherited in item.Clips) clips.Add(projection.Apply(inherited));

        // The layer's own mask is placed on the document whether or not it is linked: an
        // adjustment has no pixels of its own for a mask to share a grid with.
        if (layer.Mask is { IsEnabled: true } mask)
            clips.Add(projection.Apply(new MaskClip(mask.Coverage, mask.Placement ?? layer.Transform)));

        if (extra is MaskClip clip) clips.Add(clip);

        PixelBuffer adjusted = PixelRegion.Copy(original, new PixelRect(0, 0, original.Width, original.Height));
        try
        {
            AdjustmentRendering.Apply(layer.Adjustment!, adjusted, PixelPlacement.For(projection));

            if (layer.BlendMode != LayerBlendMode.Normal)
            {
                PixelBuffer blended = BlendAtFullCoverage(original, adjusted, layer.BlendMode, backend);
                adjusted.Release();
                adjusted = blended;
            }

            if (clips.Count == 0)
            {
                AdjustmentRendering.Mix(original, adjusted, weights: null, layer.Opacity);
            }
            else
            {
                using PixelBuffer weights = Weights(surface, clips, layer.Opacity, backend);
                AdjustmentRendering.Mix(original, adjusted, weights);
            }

            return adjusted;
        }
        catch
        {
            adjusted.Release();
            throw;
        }
    }

    /// <summary>
    /// <paramref name="top"/> blended onto <paramref name="bottom"/> as if both were opaque, with
    /// <paramref name="bottom"/>'s alpha put back. The caller owns the result.
    /// </summary>
    private static PixelBuffer BlendAtFullCoverage(PixelBuffer bottom, PixelBuffer top, LayerBlendMode mode,
                                                   IRenderBackend backend)
    {
        using ClippingGroup.Coverage alpha = ClippingGroup.ExtractAlpha(bottom);

        PixelBuffer opaqueBottom = PixelRegion.Copy(bottom, new PixelRect(0, 0, bottom.Width, bottom.Height));
        PixelBuffer opaqueTop = PixelRegion.Copy(top, new PixelRect(0, 0, top.Width, top.Height));
        try
        {
            ClippingGroup.MakeOpaque(opaqueBottom);
            ClippingGroup.MakeOpaque(opaqueTop);

            using IRenderSurface scratch = backend.CreateSurface(bottom.Width, bottom.Height);
            scratch.Write(opaqueBottom);
            scratch.Draw(new LayerDraw
            {
                Source = new BufferSource(opaqueTop) { Cacheable = false },
                Placement = Whole(scratch),
                Blend = mode,
            });

            PixelBuffer blended = scratch.Read();
            ClippingGroup.RestoreAlpha(blended, alpha);
            return blended;
        }
        finally
        {
            opaqueTop.Release();
            opaqueBottom.Release();
        }
    }

    /// <summary>
    /// How much of an adjustment each pixel of the surface takes, in the alpha channel: the opacity
    /// through every clip.
    /// </summary>
    /// <remarks>
    /// Drawn rather than computed, so that masks are sampled exactly as the renderer samples them
    /// for an ordinary layer — rotated folder masks, unlinked masks and all. What is drawn is one
    /// white pixel stretched over the surface, which costs nothing to materialise.
    /// </remarks>
    private static PixelBuffer Weights(IRenderSurface target, List<MaskClip> clips, double opacity,
                                       IRenderBackend backend)
    {
        PixelBuffer white = PixelBuffer.Allocate(1, 1);
        try
        {
            white.Row(0).Fill(255);

            using IRenderSurface surface = backend.CreateSurface(target.Width, target.Height);
            surface.Clear();
            surface.Draw(new LayerDraw
            {
                Source = new BufferSource(white) { Cacheable = false },
                Placement = Whole(surface),
                Opacity = opacity,
                Clips = clips,
            });
            return surface.Read();
        }
        finally
        {
            white.Release();
        }
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
        if (IsAdjustment(layer)) return;

        // An edit in progress stands in for the layer's own pixels — including on a blank layer,
        // which has none until the first stroke is committed.
        LiveEdit? standIn = live is LiveEdit edit && edit.LayerId == layer.Id ? edit : null;
        IPixelSource? source = standIn?.Source;
        LayerTransform? moved = standIn?.Placement;
        if (source is null)
        {
            if (layer.Image is not PixelBuffer image) return;
            source = new BufferSource(image);
        }

        var clips = new List<MaskClip>(item.Clips.Count + 2);
        foreach (MaskClip inherited in item.Clips) clips.Add(projection.Apply(inherited));

        if (layer.Mask is { IsEnabled: true, Placement: LayerTransform placement } placed)
            clips.Add(projection.Apply(new MaskClip(placed.Coverage, placement)));

        PixelBuffer? ownMask = layer.Mask is { IsEnabled: true } mask && mask.Placement is null ? mask.Coverage : null;
        if (ownMask is not null && moved is not null)
        {
            clips.Add(projection.Apply(new MaskClip(ownMask, layer.Transform)));
            ownMask = null;
        }

        if (extra is MaskClip clip) clips.Add(clip);

        surface.Draw(new LayerDraw
        {
            Source = source,
            Placement = projection.Apply(moved ?? layer.Transform),
            Opacity = layer.Opacity,
            Blend = blend ?? layer.BlendMode,
            Mask = ownMask,
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
