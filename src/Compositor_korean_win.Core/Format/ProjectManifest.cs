using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>
/// Writes a UUID the way Foundation does — uppercase.
/// </summary>
/// <remarks>
/// Upstream validates that a layer's image file is named exactly
/// <c>&lt;uuidString&gt;.png</c>, and <c>UUID.uuidString</c> is uppercase, so a manifest written
/// in .NET's default lowercase would be rejected by the macOS build. Reading stays
/// case-insensitive, because that costs nothing and a hand-edited manifest should still open.
/// </remarks>
public sealed class UppercaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) =>
        Guid.TryParse(reader.GetString(), out Guid value) ? value : throw new JsonException("not a UUID");

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}

/// <summary>One layer as it is stored. Optional fields are absent when they do not apply.</summary>
/// <remarks>
/// Every field carries the version that introduced it, and reading an older project means leaving
/// the newer ones null rather than defaulting them to something — <c>maskLinked</c> being absent
/// means linked, but <c>maskEnabled</c> being absent means there is no mask at all.
/// </remarks>
public sealed record ProjectLayerRecord
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(UppercaseGuidConverter))]
    public required Guid Id { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }
    [JsonPropertyName("isVisible")] public required bool IsVisible { get; init; }
    [JsonPropertyName("transform")] public required LayerTransform Transform { get; init; }

    /// <summary>Always <c>&lt;id&gt;.png</c>, or absent for a blank layer, a folder or an adjustment.</summary>
    [JsonPropertyName("imageFile")] public string? ImageFile { get; init; }

    /// <summary>Version 2. Absent for a root node.</summary>
    [JsonPropertyName("parentID")]
    [JsonConverter(typeof(UppercaseGuidConverter))]
    public Guid? ParentId { get; init; }

    /// <summary>Version 2.</summary>
    [JsonPropertyName("isGroup")] public bool? IsGroup { get; init; }

    /// <summary>Version 3. Absent means fully opaque.</summary>
    [JsonPropertyName("opacity")] public double? Opacity { get; init; }

    /// <summary>Version 3. Absent means Normal.</summary>
    [JsonPropertyName("blendMode")] public LayerBlendMode? BlendMode { get; init; }

    /// <summary>Version 4 for layers, version 6 for folders. Always <c>&lt;id&gt;.mask.png</c>.</summary>
    [JsonPropertyName("maskFile")] public string? MaskFile { get; init; }

    /// <summary>Version 4. Only meaningful alongside a mask; absent means enabled.</summary>
    [JsonPropertyName("maskEnabled")] public bool? MaskEnabled { get; init; }

    /// <summary>Version 5: the layer supplying live alpha — a clipping mask.</summary>
    [JsonPropertyName("maskSourceID")]
    [JsonConverter(typeof(UppercaseGuidConverter))]
    public Guid? MaskSourceId { get; init; }

    /// <summary>Version 7: this layer is an adjustment rather than pixels.</summary>
    [JsonPropertyName("adjustment")] public LayerAdjustment? Adjustment { get; init; }

    /// <summary>Where a mask moved apart from its layer sits on the document.</summary>
    [JsonPropertyName("maskPlacement")] public LayerTransform? MaskPlacement { get; init; }

    /// <summary>Absent, as in every older project, means linked.</summary>
    [JsonPropertyName("maskLinked")] public bool? MaskLinked { get; init; }

    /// <summary>A shape layer's recipe, redrawn when the layer is scaled. Older versions keep the pixels.</summary>
    [JsonPropertyName("shape")] public LayerShapeStyle? Shape { get; init; }
}

/// <summary>A project's <c>manifest.json</c>.</summary>
public sealed record ProjectManifest
{
    public const string FormatIdentifier = "com.compositor.project";

    /// <summary>The version new saves declare.</summary>
    public const int CurrentVersion = 7;

    /// <summary>The oldest version that can still be read.</summary>
    public const int OldestVersion = 1;

    [JsonPropertyName("format")] public string Format { get; init; } = FormatIdentifier;
    [JsonPropertyName("version")] public int Version { get; init; } = CurrentVersion;
    [JsonPropertyName("colorSpace")] public string ColorSpace { get; init; } = "sRGB";

    /// <summary>Pixels per inch. Absent in version-1 projects, which mean 72.</summary>
    [JsonPropertyName("resolution")] public double? Resolution { get; init; }

    [JsonPropertyName("documentID")]
    [JsonConverter(typeof(UppercaseGuidConverter))]
    public required Guid DocumentId { get; init; }

    [JsonPropertyName("width")] public required int Width { get; init; }
    [JsonPropertyName("height")] public required int Height { get; init; }

    [JsonPropertyName("activeLayerID")]
    [JsonConverter(typeof(UppercaseGuidConverter))]
    public Guid? ActiveLayerId { get; init; }

    /// <summary>Bottom to top.</summary>
    [JsonPropertyName("layers")]
    [JsonConverter(typeof(EquatableListConverter<ProjectLayerRecord>))]
    public EquatableList<ProjectLayerRecord> Layers { get; init; } = EquatableList<ProjectLayerRecord>.Empty;
}

/// <summary>Just enough of a manifest to decide whether the rest is worth decoding.</summary>
public sealed record ProjectHeader
{
    [JsonPropertyName("format")] public required string Format { get; init; }
    [JsonPropertyName("version")] public required int Version { get; init; }
}
