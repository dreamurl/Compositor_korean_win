namespace Compositor_korean_win.Core;

/// <summary>
/// The Layer menu's edits to the layer tree: adding, deleting, reordering, grouping, clipping,
/// flipping and merging.
/// </summary>
/// <remarks>
/// <para>
/// Each is a function from one document to the next, or null when it does not apply, so the shell
/// only has to wrap it in a history step and the tests can call it with no window at all. The rules
/// are upstream's (<c>EditorSession</c>, <c>LayerGroups.swift</c>, <c>LiveLayerMask.swift</c>,
/// <c>LayerFlip.swift</c>, <c>LayerMerge.swift</c>), read off its code rather than reinvented.
/// </para>
/// <para>
/// The tree lives in the flat list the format stores: bottom to top, each layer naming its folder.
/// Siblings are the layers with the same folder, in list order. A folder's contents need not sit
/// next to it in the list, so moving a folder is moving one entry.
/// </para>
/// </remarks>
public static class LayerCommands
{
    /// <summary>The first "Layer n" (or "Folder n") no layer is called yet.</summary>
    public static string NextName(CanvasDocument document, TextKey pattern)
    {
        var names = document.Layers.Select(layer => layer.Name).ToHashSet();
        for (int number = 1; ; number++)
        {
            string name = Localizer.Format(pattern, number);
            if (!names.Contains(name)) return name;
        }
    }

    /// <summary>Everything inside a folder, however deep.</summary>
    public static HashSet<Guid> Descendants(CanvasDocument document, Guid id)
    {
        ILookup<Guid?, ImageLayer> children = document.Layers.ToLookup(layer => layer.ParentId);
        var result = new HashSet<Guid>();
        var pending = new Stack<Guid>([id]);

        while (pending.Count > 0)
        {
            foreach (ImageLayer child in children[pending.Pop()])
                if (result.Add(child.Id)) pending.Push(child.Id);
        }

        return result;
    }

    /// <summary>
    /// A blank layer above the active one, in its folder — or at the top of the folder when the
    /// active layer is a folder.
    /// </summary>
    public static (CanvasDocument Document, Guid Layer) AddBlankLayer(CanvasDocument document, Guid? active)
    {
        ImageLayer? current = active is Guid id ? document.Layer(id) : null;
        var layer = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = NextName(document, TextKey.LayerNameNumbered),
            Transform = new LayerTransform(Point.Zero, document.Size),
            ParentId = current is { IsGroup: true } ? current.Id : current?.ParentId,
        };

        var layers = document.Layers.ToList();
        int insertion = current is null ? layers.Count : document.IndexOf(current.Id) + 1;

        if (current is { IsGroup: true })
        {
            HashSet<Guid> inside = Descendants(document, current.Id);
            int topmost = layers.FindLastIndex(each => inside.Contains(each.Id));
            if (topmost >= 0) insertion = Math.Max(insertion, topmost + 1);
        }

        layers.Insert(Math.Min(insertion, layers.Count), layer);
        return (document with { Layers = layers.ToEquatableList() }, layer.Id);
    }

    /// <summary>
    /// The document without these layers and whatever their folders held. Layers clipped to a
    /// deleted one stop clipping.
    /// </summary>
    public static CanvasDocument? Delete(CanvasDocument document, IEnumerable<Guid> ids)
    {
        var removed = new HashSet<Guid>();
        foreach (Guid id in ids)
        {
            if (document.Layer(id) is null) continue;
            removed.Add(id);
            removed.UnionWith(Descendants(document, id));
        }

        if (removed.Count == 0) return null;

        List<ImageLayer> layers =
        [
            .. document.Layers
                .Where(layer => !removed.Contains(layer.Id))
                .Select(layer => layer.MaskSourceId is Guid source && removed.Contains(source)
                    ? layer with { MaskSourceId = null }
                    : layer),
        ];

        ReleaseDetachedClipping(layers);
        return document with { Layers = layers.ToEquatableList() };
    }

    /// <summary>
    /// The layer to make active after <paramref name="deleted"/> went: the nearest survivor below
    /// it among its siblings, else above, else the topmost layer left.
    /// </summary>
    public static Guid? Survivor(CanvasDocument before, CanvasDocument after, Guid deleted)
    {
        if (before.Layer(deleted) is not ImageLayer layer) return after.Layers.LastOrDefault()?.Id;

        List<ImageLayer> siblings = Siblings(before, layer.ParentId);
        int index = siblings.FindIndex(each => each.Id == deleted);

        for (int i = index - 1; i >= 0; i--)
            if (after.Layer(siblings[i].Id) is not null) return siblings[i].Id;
        for (int i = index + 1; i < siblings.Count; i++)
            if (after.Layer(siblings[i].Id) is not null) return siblings[i].Id;

        return layer.ParentId is Guid parent && after.Layer(parent) is not null
            ? parent
            : after.Layers.LastOrDefault()?.Id;
    }

    /// <summary>Whether a layer has a sibling <paramref name="offset"/> places above it.</summary>
    public static bool CanMove(CanvasDocument document, Guid id, int offset)
    {
        if (document.Layer(id) is not ImageLayer layer) return false;
        List<ImageLayer> siblings = Siblings(document, layer.ParentId);
        int index = siblings.FindIndex(each => each.Id == id);
        return index + offset >= 0 && index + offset < siblings.Count;
    }

    /// <summary>A layer swapped with the sibling <paramref name="offset"/> above it (negative: below).</summary>
    public static CanvasDocument? Move(CanvasDocument document, Guid id, int offset)
    {
        if (!CanMove(document, id, offset)) return null;

        ImageLayer layer = document.Layer(id)!;
        List<ImageLayer> siblings = Siblings(document, layer.ParentId);
        Guid other = siblings[siblings.FindIndex(each => each.Id == id) + offset].Id;

        var layers = document.Layers.ToList();
        int a = layers.FindIndex(each => each.Id == id), b = layers.FindIndex(each => each.Id == other);
        (layers[a], layers[b]) = (layers[b], layers[a]);

        return document with { Layers = layers.ToEquatableList() };
    }

    /// <summary>
    /// A new folder holding the selected layers, at the topmost of them, in the folder they all
    /// share. A selected folder brings its contents; a layer selected inside it stays where it is.
    /// </summary>
    public static (CanvasDocument Document, Guid Folder)? Group(CanvasDocument document, IReadOnlyCollection<Guid> selected)
    {
        Dictionary<Guid, ImageLayer> byId = document.Layers.ToDictionary(layer => layer.Id);
        var chosen = selected.Where(byId.ContainsKey).ToHashSet();
        if (chosen.Count == 0) return null;

        List<Guid?> Ancestors(Guid id)
        {
            var result = new List<Guid?>();
            Guid? up = byId[id].ParentId;
            while (up is Guid container && byId.ContainsKey(container) && result.Count < 256)
            {
                result.Add(container);
                up = byId[container].ParentId;
            }
            result.Add(null);
            return result;
        }

        var roots = chosen.Where(id => !Ancestors(id).Any(ancestor => ancestor is Guid a && chosen.Contains(a))).ToHashSet();
        List<Guid> ordered = [.. document.Layers.Select(layer => layer.Id).Where(roots.Contains)];

        // The deepest folder every chosen layer is inside.
        Guid? parent = Ancestors(ordered[0]).FirstOrDefault(candidate => ordered.All(id => Ancestors(id).Contains(candidate)));

        var folder = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = NextName(document, TextKey.FolderNameNumbered),
            Transform = new LayerTransform(Point.Zero, document.Size),
            IsGroup = true,
            ParentId = parent,
        };

        // The folder takes the place of the topmost chosen branch within the shared parent.
        var branches = ordered.Select(id =>
        {
            Guid branch = id;
            while (byId[branch].ParentId is Guid next && next != parent && byId.ContainsKey(next)) branch = next;
            return branch;
        }).ToHashSet();

        List<ImageLayer> all = [.. document.Layers];
        int highest = all.FindLastIndex(layer => branches.Contains(layer.Id));
        int insertion = highest < 0 ? all.Count : all.Take(highest + 1).Count(layer => !roots.Contains(layer.Id));

        List<ImageLayer> layers = [.. all.Where(layer => !roots.Contains(layer.Id))];
        layers.Insert(Math.Min(insertion, layers.Count), folder);
        layers.AddRange(ordered.Select(id => byId[id] with { ParentId = folder.Id }));

        ReleaseDetachedClipping(layers);
        return (document with { Layers = layers.ToEquatableList() }, folder.Id);
    }

    /// <summary>A layer taken out of its folder and put just above it.</summary>
    public static CanvasDocument? MoveOutOfFolder(CanvasDocument document, Guid id)
    {
        if (document.Layer(id) is not { ParentId: Guid folderId } layer
            || document.Layer(folderId) is not ImageLayer folder) return null;

        var layers = document.Layers.Where(each => each.Id != id).ToList();
        int above = layers.FindIndex(each => each.Id == folder.Id) + 1;
        layers.Insert(above, layer with { ParentId = folder.ParentId, MaskSourceId = null });

        ReleaseDetachedClipping(layers);
        return document with { Layers = layers.ToEquatableList() };
    }

    public static CanvasDocument ToggleVisibility(CanvasDocument document, Guid id) =>
        document.Layer(id) is ImageLayer layer ? document.Replacing(layer with { IsVisible = !layer.IsVisible }) : document;

    /// <summary>The same document with a layer renamed; a blank name is refused.</summary>
    public static CanvasDocument? Rename(CanvasDocument document, Guid id, string name)
    {
        name = name.Trim();
        if (name.Length == 0 || document.Layer(id) is not ImageLayer layer || layer.Name == name) return null;
        return document.Replacing(layer with { Name = name });
    }

    /// <summary>Whether a layer is clipped to the one below it.</summary>
    public static bool IsClipped(CanvasDocument document, Guid id) => document.Layer(id)?.MaskSourceId is not null;

    /// <summary>
    /// Clips a layer to the sibling below it — sharing that sibling's base if it is clipped already —
    /// or, if it is clipped, releases it and the layers clipped above it to the same base.
    /// </summary>
    public static CanvasDocument? ToggleClipping(CanvasDocument document, Guid id)
    {
        if (document.Layer(id) is not { IsGroup: false } layer) return null;

        List<ImageLayer> siblings = Siblings(document, layer.ParentId);
        int index = siblings.FindIndex(each => each.Id == id);
        var layers = document.Layers.ToList();

        if (layer.MaskSourceId is Guid source)
        {
            var releases = siblings.Skip(index)
                .TakeWhile(each => each.Id == id || each.MaskSourceId == source)
                .Select(each => each.Id)
                .ToHashSet();

            for (int i = 0; i < layers.Count; i++)
                if (releases.Contains(layers[i].Id)) layers[i] = layers[i] with { MaskSourceId = null };

            return document with { Layers = layers.ToEquatableList() };
        }

        if (index <= 0 || siblings[index - 1].IsGroup) return null;

        Guid baseId = siblings[index - 1].MaskSourceId ?? siblings[index - 1].Id;
        return document.Replacing(layer with { MaskSourceId = baseId });
    }

    /// <summary>
    /// Layers mirrored across a line: each about its own middle when there is one, several about the
    /// middle of the box around them. A linked mask goes with its layer; an unlinked one stays put.
    /// </summary>
    public static CanvasDocument? Flip(CanvasDocument document, IReadOnlyCollection<Guid> ids, bool horizontally)
    {
        List<ImageLayer> members = [.. document.Layers.Where(layer => ids.Contains(layer.Id) && !layer.IsGroup)];
        if (members.Count == 0) return null;

        Rect box = Rect.Around([.. members.SelectMany(Corners)]);
        double axis = horizontally ? box.X + box.Width / 2 : box.Y + box.Height / 2;

        CanvasDocument result = document;
        foreach (ImageLayer layer in members)
            result = result.Replacing(layer with { Transform = Mirrored(layer.Transform, horizontally, axis) });

        return result;
    }

    /// <summary>
    /// The whole canvas mirrored across its middle: every layer, folder and placed mask. The
    /// selection is the caller's to mirror, since it is not part of the document.
    /// </summary>
    public static CanvasDocument FlipCanvas(CanvasDocument document, bool horizontally)
    {
        double axis = horizontally ? document.Width / 2.0 : document.Height / 2.0;

        return document with
        {
            Layers = document.Layers.Select(layer => layer with
            {
                Transform = Mirrored(layer.Transform, horizontally, axis),
                Mask = layer.Mask is { Placement: LayerTransform placement } mask
                    ? mask with { Placement = Mirrored(placement, horizontally, axis) }
                    : layer.Mask,
            }).ToEquatableList(),
        };
    }

    /// <summary>
    /// A placement mirrored across a vertical line at <paramref name="axis"/> (or, not
    /// <paramref name="horizontally"/>, a horizontal one): the picture flips, its angle turns the
    /// other way, and its middle crosses to the other side.
    /// </summary>
    public static LayerTransform Mirrored(LayerTransform transform, bool horizontally, double axis)
    {
        Point centre = transform.Center;
        return horizontally
            ? transform with
            {
                FlipX = !transform.FlipX,
                Rotation = -transform.Rotation,
                Origin = new Point(2 * axis - centre.X - transform.Size.Width / 2, transform.Origin.Y),
            }
            : transform with
            {
                FlipY = !transform.FlipY,
                Rotation = -transform.Rotation,
                Origin = new Point(transform.Origin.X, 2 * axis - centre.Y - transform.Size.Height / 2),
            };
    }

    /// <summary>What Merge would do right now, or null when there is nothing to merge.</summary>
    public sealed record MergePlan(IReadOnlyList<Guid> Ids, IReadOnlySet<Guid> Removed, string Name,
                                   Guid? Parent, Guid Anchor, TextKey Action);

    /// <summary>
    /// Upstream's rule: several chosen layers merge together, with whatever their folders hold; a
    /// folder merges its contents and goes; one layer merges down into the sibling beneath it.
    /// </summary>
    public static MergePlan? PlanMerge(CanvasDocument document, IReadOnlyCollection<Guid> selected, Guid? active)
    {
        if (active is not Guid activeId || document.Layer(activeId) is not ImageLayer current) return null;
        List<ImageLayer> layers = [.. document.Layers];

        if (selected.Count > 1)
        {
            var picked = selected.Where(id => document.Layer(id) is not null).ToHashSet();
            foreach (Guid id in picked.ToList()) picked.UnionWith(Descendants(document, id));

            List<ImageLayer> ordered = [.. layers.Where(layer => picked.Contains(layer.Id))];
            if (!ordered.Any(layer => !layer.IsGroup)) return null;
            if (ordered.LastOrDefault(layer => selected.Contains(layer.Id)) is not ImageLayer top) return null;

            return new MergePlan([.. ordered.Select(layer => layer.Id)], picked, top.Name, top.ParentId, top.Id,
                                 TextKey.CommandMergeLayers);
        }

        if (current.IsGroup)
        {
            HashSet<Guid> inside = Descendants(document, current.Id);
            if (!layers.Any(layer => inside.Contains(layer.Id) && !layer.IsGroup)) return null;

            List<Guid> ids = [.. layers.Where(layer => inside.Contains(layer.Id) || layer.Id == current.Id).Select(layer => layer.Id)];
            return new MergePlan(ids, ids.ToHashSet(), current.Name, current.ParentId, current.Id, TextKey.CommandMergeGroup);
        }

        int index = layers.FindIndex(layer => layer.Id == current.Id);
        ImageLayer? below = layers.Take(index).LastOrDefault(layer => layer.ParentId == current.ParentId);
        if (below is null || below.IsGroup) return null;

        return new MergePlan([below.Id, current.Id], new HashSet<Guid> { below.Id, current.Id }, below.Name,
                             current.ParentId, current.Id, TextKey.CommandMergeDown);
    }

    /// <summary>
    /// The plan's layers composited as the canvas shows them — blend modes, opacity, masks, clipping
    /// and adjustments baked in — into one pixel layer, trimmed to what is there, in their place.
    /// </summary>
    public static (CanvasDocument Document, Guid Layer)? Merge(CanvasDocument document, MergePlan plan)
    {
        var kept = plan.Ids.ToHashSet();

        // Only the merged layers, cut loose from anything outside the merge.
        List<ImageLayer> subset =
        [
            .. document.Layers.Where(layer => kept.Contains(layer.Id)).Select(layer => layer with
            {
                ParentId = layer.ParentId is Guid parent && kept.Contains(parent) ? parent : null,
                MaskSourceId = layer.MaskSourceId is Guid source && kept.Contains(source) ? source : null,
            }),
        ];

        using var backend = new SoftwareRenderBackend();
        using PixelBuffer full = LayerCompositor.Render(document with { Layers = subset.ToEquatableList() }, backend);

        PixelRect trim = LayerFilters.Trim(full);
        if (trim.IsEmpty) return null;

        var merged = new ImageLayer
        {
            Id = Guid.NewGuid(),
            Name = plan.Name,
            Image = PixelRegion.Copy(full, trim),
            Transform = new LayerTransform(new Point(trim.X, trim.Y), new Size(trim.Width, trim.Height)),
            ParentId = plan.Parent,
        };

        List<ImageLayer> all = [.. document.Layers];
        List<ImageLayer> next =
        [
            .. all.Where(layer => !plan.Removed.Contains(layer.Id)).Select(layer =>
                layer.MaskSourceId is Guid source && plan.Removed.Contains(source)
                    ? layer with { MaskSourceId = merged.Id }
                    : layer),
        ];

        int slot = all.FindIndex(layer => layer.Id == plan.Anchor);
        if (slot < 0) slot = all.Count;
        int insertion = slot - all.Take(slot).Count(layer => plan.Removed.Contains(layer.Id));
        next.Insert(Math.Clamp(insertion, 0, next.Count), merged);

        return (document with { Layers = next.ToEquatableList() }, merged.Id);
    }

    /// <summary>
    /// A layer stops clipping once it is no longer in the unbroken run of clipped layers directly
    /// above its base, among its siblings.
    /// </summary>
    public static void ReleaseDetachedClipping(List<ImageLayer> layers)
    {
        var release = new HashSet<Guid>();
        foreach (IGrouping<Guid?, ImageLayer> stack in layers.GroupBy(layer => layer.ParentId))
        {
            Guid? baseId = null;
            foreach (ImageLayer layer in stack)
            {
                if (layer.MaskSourceId is Guid source)
                {
                    // Released, it is a base of its own for whatever is clipped above it.
                    if (source != baseId)
                    {
                        release.Add(layer.Id);
                        baseId = layer.Id;
                    }
                }
                else
                {
                    baseId = layer.IsGroup ? null : layer.Id;
                }
            }
        }

        for (int i = 0; i < layers.Count; i++)
            if (release.Contains(layers[i].Id)) layers[i] = layers[i] with { MaskSourceId = null };
    }

    private static List<ImageLayer> Siblings(CanvasDocument document, Guid? parent) =>
        [.. document.Layers.Where(layer => layer.ParentId == parent)];

    private static IEnumerable<Point> Corners(ImageLayer layer) =>
    [
        layer.Transform.PointAt(new Point(0, 0)), layer.Transform.PointAt(new Point(1, 0)),
        layer.Transform.PointAt(new Point(0, 1)), layer.Transform.PointAt(new Point(1, 1)),
    ];
}
