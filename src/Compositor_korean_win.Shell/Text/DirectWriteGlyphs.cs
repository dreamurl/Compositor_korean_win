using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Compositor_korean_win.Core;
using Point = Compositor_korean_win.Core.Point;

namespace Compositor_korean_win.Shell;

[StructLayout(LayoutKind.Sequential)]
internal struct OutlinePoint
{
    public float X;
    public float Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct OutlineBezier
{
    public OutlinePoint Control1;
    public OutlinePoint Control2;
    public OutlinePoint End;
}

/// <summary>ID2D1SimplifiedGeometrySink, which DirectWrite calls IDWriteGeometrySink.</summary>
[GeneratedComInterface]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid("2cd9069e-12e2-11dc-9fed-001143a055f9")]
internal unsafe partial interface IOutlineSink
{
    [PreserveSig] void SetFillMode(int mode);
    [PreserveSig] void SetSegmentFlags(int flags);
    [PreserveSig] void BeginFigure(OutlinePoint start, int begin);
    [PreserveSig] void AddLines(OutlinePoint* points, uint count);
    [PreserveSig] void AddBeziers(OutlineBezier* beziers, uint count);
    [PreserveSig] void EndFigure(int end);
    [PreserveSig] int Close();
}

/// <summary>Collects one glyph's outline as DirectWrite hands it over.</summary>
[GeneratedComClass]
internal sealed unsafe partial class OutlineCollector : IOutlineSink
{
    private readonly float _scale;
    private Point _start;
    private List<GlyphSegment>? _segments;

    public OutlineCollector(float emSize) => _scale = 1f / emSize;

    public List<GlyphFigure> Figures { get; } = [];

    private Point Em(OutlinePoint point) => new(point.X * _scale, point.Y * _scale);

    public void SetFillMode(int mode) { }

    public void SetSegmentFlags(int flags) { }

    public void BeginFigure(OutlinePoint start, int begin)
    {
        _start = Em(start);
        _segments = [];
    }

    public void AddLines(OutlinePoint* points, uint count)
    {
        for (uint i = 0; i < count; i++) _segments?.Add(GlyphSegment.Line(Em(points[i])));
    }

    public void AddBeziers(OutlineBezier* beziers, uint count)
    {
        for (uint i = 0; i < count; i++)
            _segments?.Add(GlyphSegment.Curve(Em(beziers[i].Control1), Em(beziers[i].Control2), Em(beziers[i].End)));
    }

    public void EndFigure(int end)
    {
        if (_segments is { Count: > 0 }) Figures.Add(new GlyphFigure(_start, _segments));
        _segments = null;
    }

    public int Close() => 0;
}

/// <summary>
/// Glyph outlines from the fonts installed in Windows, through DirectWrite.
/// </summary>
/// <remarks>
/// <para>
/// Every call goes through the COM tables directly rather than through the Vortice wrappers: the
/// wrappers' font calls lean on reflection the trimmed NativeAOT build does not keep, which is why
/// <see cref="Ui"/> already reaches CreateTextFormat the same way. The slot numbers are the
/// interfaces' declaration order in dwrite.h.
/// </para>
/// <para>
/// A character the chosen font has no glyph for comes from Malgun Gothic instead, then from
/// Segoe UI Symbol — what Windows itself falls back to for Hangul and symbols — so Korean typed
/// in a Latin display face still shows rather than drawing boxes.
/// </para>
/// </remarks>
internal sealed unsafe class DirectWriteGlyphs : IGlyphSource, IDisposable
{
    private const float EmSize = 2048;
    private static readonly string[] Fallbacks = ["Malgun Gothic", "Segoe UI Symbol", "Segoe UI Emoji"];

    [StructLayout(LayoutKind.Sequential)]
    private struct FontMetrics
    {
        public ushort DesignUnitsPerEm, Ascent, Descent;
        public short LineGap;
        public ushort CapHeight, XHeight;
        public short UnderlinePosition;
        public ushort UnderlineThickness;
        public short StrikethroughPosition;
        public ushort StrikethroughThickness;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GlyphMetrics
    {
        public int LeftSideBearing;
        public uint AdvanceWidth;
        public int RightSideBearing, TopSideBearing;
        public uint AdvanceHeight;
        public int BottomSideBearing, VerticalOriginY;
    }

    private sealed record Face(nint Pointer, double UnitsPerEm, FontMetricsEm Metrics);

    private readonly nint _factory;
    private readonly nint _collection;
    private readonly Dictionary<TextFace, Face?> _faces = [];
    private readonly Dictionary<(TextFace, int), GlyphShape> _glyphs = [];
    private readonly Dictionary<bool, IReadOnlyList<FontFamilyName>> _families = [];

    public DirectWriteGlyphs()
    {
        var iid = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        int result = Win32.DWriteCreateFactory(0, iid, out _factory);
        if (result < 0 || _factory == 0) throw new InvalidOperationException($"DWriteCreateFactory failed: 0x{result:X8}");

        nint collection;
        result = ((delegate* unmanaged[Stdcall]<nint, nint*, int, int>)Slot(_factory, 3))(_factory, &collection, 0);
        if (result < 0 || collection == 0) throw new InvalidOperationException($"GetSystemFontCollection failed: 0x{result:X8}");
        _collection = collection;
    }

    /// <summary>The one every text layer is set with, made on first use.</summary>
    public static DirectWriteGlyphs Shared => s_shared ??= new DirectWriteGlyphs();
    private static DirectWriteGlyphs? s_shared;

    private static nint Slot(nint instance, int index) => (*(nint**)instance)[index];

    private static void ReleaseCom(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(instance, 2))(instance);
    }

    public FontMetricsEm Metrics(TextFace face) =>
        Resolve(face)?.Metrics ?? new FontMetricsEm(0.9, 0.25, 0);

    public GlyphShape Glyph(TextFace face, int codepoint)
    {
        if (_glyphs.TryGetValue((face, codepoint), out GlyphShape? cached)) return cached;

        GlyphShape shape = GlyphFrom(face, codepoint) ?? FallbackGlyph(face, codepoint) ?? GlyphShape.Blank(0.5);
        _glyphs[(face, codepoint)] = shape;
        return shape;
    }

    private GlyphShape? FallbackGlyph(TextFace face, int codepoint)
    {
        foreach (string family in Fallbacks)
        {
            if (string.Equals(family, face.Family, StringComparison.OrdinalIgnoreCase)) continue;
            if (GlyphFrom(face with { Family = family }, codepoint) is GlyphShape shape) return shape;
        }
        return null;
    }

    /// <summary>The glyph from this very face, or null when it has none for the character.</summary>
    private GlyphShape? GlyphFrom(TextFace face, int codepoint)
    {
        if (Resolve(face) is not Face resolved) return null;

        uint point = (uint)codepoint;
        ushort index;
        int result = ((delegate* unmanaged[Stdcall]<nint, uint*, uint, ushort*, int>)Slot(resolved.Pointer, 11))(
            resolved.Pointer, &point, 1, &index);
        if (result < 0) return null;
        // Glyph 0 is .notdef, the box a font draws for what it lacks. A space is glyph-less in some
        // fonts too, but those still report a real index for it.
        if (index == 0) return null;

        GlyphMetrics metrics;
        result = ((delegate* unmanaged[Stdcall]<nint, ushort*, uint, GlyphMetrics*, int, int>)Slot(resolved.Pointer, 10))(
            resolved.Pointer, &index, 1, &metrics, 0);
        double advance = result >= 0 ? metrics.AdvanceWidth / resolved.UnitsPerEm : 0.5;

        var collector = new OutlineCollector(EmSize);
        void* sink = ComInterfaceMarshaller<IOutlineSink>.ConvertToUnmanaged(collector);
        try
        {
            float zero = 0;
            result = ((delegate* unmanaged[Stdcall]<nint, float, ushort*, float*, void*, uint, int, int, void*, int>)Slot(resolved.Pointer, 14))(
                resolved.Pointer, EmSize, &index, &zero, null, 1, 0, 0, sink);
        }
        finally
        {
            ComInterfaceMarshaller<IOutlineSink>.Free(sink);
        }

        return new GlyphShape(advance, result >= 0 ? collector.Figures : []);
    }

    private readonly Dictionary<nint, FontKerning?> _kerning = [];
    private readonly Dictionary<(nint, int), ushort> _indices = [];

    /// <summary>
    /// The face's own pair kerning (GPOS <c>kern</c>, or the old <c>kern</c> table) between two
    /// characters, in ems. A character the face lacks is drawn from a fallback font, which the pair
    /// table knows nothing of, so it kerns by nothing.
    /// </summary>
    public double Kerning(TextFace face, int left, int right)
    {
        if (Resolve(face) is not Face resolved) return 0;
        ushort first = GlyphIndex(resolved, left), second = GlyphIndex(resolved, right);
        if (first == 0 || second == 0) return 0;

        if (!_kerning.TryGetValue(resolved.Pointer, out FontKerning? kerning))
        {
            kerning = FontKerning.Read(Table(resolved.Pointer, "GPOS"), Table(resolved.Pointer, "kern"), resolved.UnitsPerEm);
            _kerning[resolved.Pointer] = kerning;
        }
        return kerning?.Pair(first, second) ?? 0;
    }

    private ushort GlyphIndex(Face face, int codepoint)
    {
        if (_indices.TryGetValue((face.Pointer, codepoint), out ushort known)) return known;
        uint point = (uint)codepoint;
        ushort index = 0;
        int result = ((delegate* unmanaged[Stdcall]<nint, uint*, uint, ushort*, int>)Slot(face.Pointer, 11))(
            face.Pointer, &point, 1, &index);
        if (result < 0) index = 0;
        _indices[(face.Pointer, codepoint)] = index;
        return index;
    }

    /// <summary>A whole OpenType table of the face, copied out; empty when the font has none.</summary>
    private static byte[] Table(nint fontFace, string tag)
    {
        // DWRITE_MAKE_OPENTYPE_TAG: the first letter in the low byte.
        uint code = (uint)(tag[0] | tag[1] << 8 | tag[2] << 16 | tag[3] << 24);
        void* data;
        uint size;
        void* context;
        int exists;
        int result = ((delegate* unmanaged[Stdcall]<nint, uint, void**, uint*, void**, int*, int>)Slot(fontFace, 12))(
            fontFace, code, &data, &size, &context, &exists);
        if (result < 0 || exists == 0 || data == null) return [];
        try
        {
            return new ReadOnlySpan<byte>(data, (int)size).ToArray();
        }
        finally
        {
            ((delegate* unmanaged[Stdcall]<nint, void*, void>)Slot(fontFace, 13))(fontFace, context);
        }
    }

    /// <summary>A family, weight and slant made into a font face — or null when no such family is installed.</summary>
    private Face? Resolve(TextFace face)
    {
        if (_faces.TryGetValue(face, out Face? known)) return known;

        Face? made = null;
        nint family = 0, font = 0, fontFace = 0;
        try
        {
            uint index;
            int exists;
            int result;
            fixed (char* name = face.Family)
            {
                result = ((delegate* unmanaged[Stdcall]<nint, char*, uint*, int*, int>)Slot(_collection, 5))(
                    _collection, name, &index, &exists);
            }

            if (result >= 0 && exists != 0
                && ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_collection, 4))(_collection, index, &family) >= 0
                && ((delegate* unmanaged[Stdcall]<nint, int, int, int, nint*, int>)Slot(family, 7))(
                       family, Math.Clamp(face.Weight, 1, 999), 5, face.Italic ? 2 : 0, &font) >= 0
                && ((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(font, 13))(font, &fontFace) >= 0)
            {
                FontMetrics metrics;
                ((delegate* unmanaged[Stdcall]<nint, FontMetrics*, void>)Slot(fontFace, 8))(fontFace, &metrics);
                double units = Math.Max(1, (int)metrics.DesignUnitsPerEm);
                made = new Face(fontFace, units,
                    new FontMetricsEm(metrics.Ascent / units, metrics.Descent / units, metrics.LineGap / units));
                fontFace = 0;
            }
        }
        finally
        {
            ReleaseCom(fontFace);
            ReleaseCom(font);
            ReleaseCom(family);
        }

        _faces[face] = made;
        return made;
    }

    /// <summary>A family as the font menu shows it, and the name it is stored and found by.</summary>
    public sealed record FontFamilyName(string Shown, string Name);

    /// <summary>Every installed family, sorted by the name shown.</summary>
    /// <remarks>
    /// Stored by its English name so a project opens the same on a Windows set to any language;
    /// shown by the name for the interface's language when the font has one, so Malgun Gothic
    /// reads under its Korean name in the Korean interface.
    /// </remarks>
    public IReadOnlyList<FontFamilyName> Families(bool korean)
    {
        if (_families.TryGetValue(korean, out IReadOnlyList<FontFamilyName>? known)) return known;

        var result = new List<FontFamilyName>();
        uint count = ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(_collection, 3))(_collection);
        for (uint i = 0; i < count; i++)
        {
            nint family = 0, names = 0;
            try
            {
                if (((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(_collection, 4))(_collection, i, &family) < 0) continue;
                if (((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(family, 6))(family, &names) < 0) continue;

                string? english = Localized(names, "en-us");
                string? shown = korean ? Localized(names, "ko-kr") : null;
                english ??= Localized(names, null);
                if (english is null || english.StartsWith('@')) continue;
                result.Add(new FontFamilyName(shown ?? english, english));
            }
            finally
            {
                ReleaseCom(names);
                ReleaseCom(family);
            }
        }

        result.Sort((a, b) => string.Compare(a.Shown, b.Shown, StringComparison.CurrentCultureIgnoreCase));
        _families[korean] = result;
        return result;
    }

    /// <summary>A localized-strings entry for a locale, or its first entry when <paramref name="locale"/> is null.</summary>
    private static string? Localized(nint strings, string? locale)
    {
        uint index = 0;
        if (locale is not null)
        {
            int exists;
            int found;
            fixed (char* name = locale)
            {
                found = ((delegate* unmanaged[Stdcall]<nint, char*, uint*, int*, int>)Slot(strings, 4))(strings, name, &index, &exists);
            }
            if (found < 0 || exists == 0) return null;
        }

        uint length;
        if (((delegate* unmanaged[Stdcall]<nint, uint, uint*, int>)Slot(strings, 7))(strings, index, &length) < 0) return null;
        char[] buffer = new char[length + 1];
        fixed (char* text = buffer)
        {
            if (((delegate* unmanaged[Stdcall]<nint, uint, char*, uint, int>)Slot(strings, 8))(strings, index, text, length + 1) < 0)
                return null;
        }
        return new string(buffer, 0, (int)length);
    }

    public void Dispose()
    {
        foreach (Face? face in _faces.Values) if (face is not null) ReleaseCom(face.Pointer);
        _faces.Clear();
        ReleaseCom(_collection);
        ReleaseCom(_factory);
    }
}
