using System.Buffers.Binary;

namespace Compositor_korean_win.Core;

/// <summary>
/// A font's pair kerning — how much closer or further apart two glyphs sit than their advances say.
/// </summary>
/// <remarks>
/// <para>
/// Photoshop's default kerning, "Metrics", is the font's own: the pair adjustments in its GPOS
/// table's <c>kern</c> feature, or in an older font the <c>kern</c> table. Setting type without
/// them is the most visible difference from Photoshop — "AV", "To", "Wa" gape — so text read from a
/// PSD would reflow wider the moment it was edited. This reads both, from the table bytes the
/// shell's font engine hands over, and needs nothing past <c>System.*</c>.
/// </para>
/// <para>
/// Only pair adjustment (GPOS lookup type 2, and type 9 extensions of it) is read, and only its
/// first glyph's advance — what kerning is. Script and language selection is not: every lookup the
/// <c>kern</c> feature names applies, which is what a Latin or Hangul line gets anyway.
/// </para>
/// </remarks>
public sealed class FontKerning
{
    private readonly byte[] _gpos;
    private readonly List<(int Table, List<int> Subtables)> _lookups = [];
    private readonly Dictionary<(ushort, ushort), short>? _legacy;
    private readonly Dictionary<(ushort, ushort), int> _cache = [];

    /// <summary>Font units to the em.</summary>
    public double UnitsPerEm { get; }

    private FontKerning(byte[] gpos, Dictionary<(ushort, ushort), short>? legacy, double unitsPerEm)
    {
        _gpos = gpos;
        _legacy = legacy;
        UnitsPerEm = unitsPerEm;
    }

    /// <summary>
    /// The kerning in a font's GPOS and kern tables, either of which may be empty; null when neither
    /// has any, so a font without kerning costs nothing per pair.
    /// </summary>
    public static FontKerning? Read(byte[] gpos, byte[] kern, double unitsPerEm)
    {
        var kerning = new FontKerning(gpos, ReadLegacy(kern), Math.Max(1, unitsPerEm));
        try
        {
            kerning.FindLookups();
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // A damaged table kerns nothing rather than failing the text.
            kerning._lookups.Clear();
        }
        return kerning._lookups.Count > 0 || kerning._legacy is { Count: > 0 } ? kerning : null;
    }

    /// <summary>The adjustment between two glyphs, in ems: negative draws them together.</summary>
    public double Pair(ushort left, ushort right)
    {
        if (!_cache.TryGetValue((left, right), out int units))
        {
            units = 0;
            try
            {
                units = _lookups.Count > 0 ? FromLookups(left, right) : 0;
            }
            catch (Exception exception) when (exception is ArgumentOutOfRangeException or IndexOutOfRangeException)
            {
                units = 0;
            }
            if (_lookups.Count == 0 && _legacy is not null && _legacy.TryGetValue((left, right), out short value)) units = value;
            _cache[(left, right)] = units;
        }
        return units / UnitsPerEm;
    }

    private ushort U16(int at) => BinaryPrimitives.ReadUInt16BigEndian(_gpos.AsSpan(at, 2));

    private short I16(int at) => BinaryPrimitives.ReadInt16BigEndian(_gpos.AsSpan(at, 2));

    private uint U32(int at) => BinaryPrimitives.ReadUInt32BigEndian(_gpos.AsSpan(at, 4));

    /// <summary>Every pair-adjustment subtable the kern feature reaches, in lookup order.</summary>
    private void FindLookups()
    {
        if (_gpos.Length < 10 || U16(0) != 1) return;
        int features = U16(6), lookups = U16(8);

        var indices = new SortedSet<int>();
        int featureCount = U16(features);
        for (int i = 0; i < featureCount; i++)
        {
            int record = features + 2 + i * 6;
            if (System.Text.Encoding.ASCII.GetString(_gpos, record, 4) != "kern") continue;
            int feature = features + U16(record + 4);
            int count = U16(feature + 2);
            for (int k = 0; k < count; k++) indices.Add(U16(feature + 4 + k * 2));
        }

        int lookupCount = U16(lookups);
        foreach (int index in indices)
        {
            if (index >= lookupCount) continue;
            int lookup = lookups + U16(lookups + 2 + index * 2);
            int type = U16(lookup);
            int subtableCount = U16(lookup + 4);
            var subtables = new List<int>();
            for (int s = 0; s < subtableCount; s++)
            {
                int subtable = lookup + U16(lookup + 6 + s * 2);
                if (type == 9)
                {
                    // An extension: the real subtable is further on, and says its own type.
                    if (U16(subtable + 2) != 2) continue;
                    subtable += (int)U32(subtable + 4);
                }
                else if (type != 2)
                {
                    continue;
                }
                subtables.Add(subtable);
            }
            if (subtables.Count > 0) _lookups.Add((lookup, subtables));
        }
    }

    /// <summary>The sum over the kern lookups of the first subtable in each that has the pair.</summary>
    private int FromLookups(ushort left, ushort right)
    {
        int total = 0;
        foreach ((_, List<int> subtables) in _lookups)
        {
            foreach (int subtable in subtables)
            {
                if (PairPos(subtable, left, right) is int adjustment)
                {
                    total += adjustment;
                    break;
                }
            }
        }
        return total;
    }

    /// <summary>One PairPos subtable's advance change for the first glyph, or null when it does not cover the pair.</summary>
    private int? PairPos(int table, ushort left, ushort right)
    {
        int format = U16(table);
        if (Coverage(table + U16(table + 2), left) is not int covered) return null;
        int format1 = U16(table + 4), format2 = U16(table + 6);
        int size1 = ValueSize(format1), size2 = ValueSize(format2);

        if (format == 1)
        {
            int set = table + U16(table + 10 + covered * 2);
            int count = U16(set);
            int record = 2 + size1 + size2;
            // The second glyphs are sorted: a binary search finds the pair.
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int at = set + 2 + middle * record;
                ushort second = U16(at);
                if (second == right) return XAdvance(at + 2, format1);
                if (second < right) low = middle + 1;
                else high = middle - 1;
            }
            return null;
        }

        if (format == 2)
        {
            int class1 = ClassOf(table + U16(table + 8), left);
            int class2 = ClassOf(table + U16(table + 10), right);
            int class1Count = U16(table + 12), class2Count = U16(table + 14);
            if (class1 >= class1Count || class2 >= class2Count) return 0;
            int at = table + 16 + (class1 * class2Count + class2) * (size1 + size2);
            return XAdvance(at, format1);
        }

        return null;
    }

    /// <summary>A value record's size: two bytes for each field its format says it has.</summary>
    private static int ValueSize(int format) => 2 * System.Numerics.BitOperations.PopCount((uint)format & 0xFF);

    /// <summary>The XAdvance field of a value record, 0 when the format has none.</summary>
    private int XAdvance(int at, int format)
    {
        if ((format & 0x0004) == 0) return 0;
        int offset = 0;
        if ((format & 0x0001) != 0) offset += 2;
        if ((format & 0x0002) != 0) offset += 2;
        return I16(at + offset);
    }

    /// <summary>A glyph's index in a coverage table, or null when it is not covered.</summary>
    private int? Coverage(int table, ushort glyph)
    {
        int format = U16(table), count = U16(table + 2);
        if (format == 1)
        {
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                ushort found = U16(table + 4 + middle * 2);
                if (found == glyph) return middle;
                if (found < glyph) low = middle + 1;
                else high = middle - 1;
            }
            return null;
        }
        if (format == 2)
        {
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int range = table + 4 + middle * 6;
                ushort start = U16(range), end = U16(range + 2);
                if (glyph < start) high = middle - 1;
                else if (glyph > end) low = middle + 1;
                else return U16(range + 4) + glyph - start;
            }
        }
        return null;
    }

    /// <summary>A glyph's class in a class definition table; 0 for any glyph it does not list.</summary>
    private int ClassOf(int table, ushort glyph)
    {
        int format = U16(table);
        if (format == 1)
        {
            int start = U16(table + 2), count = U16(table + 4);
            int index = glyph - start;
            return index >= 0 && index < count ? U16(table + 6 + index * 2) : 0;
        }
        if (format == 2)
        {
            int count = U16(table + 2);
            int low = 0, high = count - 1;
            while (low <= high)
            {
                int middle = (low + high) / 2;
                int range = table + 4 + middle * 6;
                ushort start = U16(range), end = U16(range + 2);
                if (glyph < start) high = middle - 1;
                else if (glyph > end) low = middle + 1;
                else return U16(range + 4);
            }
        }
        return 0;
    }

    /// <summary>The first horizontal format-0 subtable of an old-style kern table.</summary>
    private static Dictionary<(ushort, ushort), short>? ReadLegacy(byte[] kern)
    {
        try
        {
            if (kern.Length < 4 || BinaryPrimitives.ReadUInt16BigEndian(kern) != 0) return null;
            int tables = BinaryPrimitives.ReadUInt16BigEndian(kern.AsSpan(2));
            int at = 4;
            for (int t = 0; t < tables && at + 6 <= kern.Length; t++)
            {
                int length = BinaryPrimitives.ReadUInt16BigEndian(kern.AsSpan(at + 2));
                int coverage = BinaryPrimitives.ReadUInt16BigEndian(kern.AsSpan(at + 4));
                if ((coverage >> 8) == 0 && (coverage & 1) != 0)
                {
                    int pairs = BinaryPrimitives.ReadUInt16BigEndian(kern.AsSpan(at + 6));
                    var found = new Dictionary<(ushort, ushort), short>(pairs);
                    for (int p = 0; p < pairs; p++)
                    {
                        int pair = at + 14 + p * 6;
                        if (pair + 6 > kern.Length) break;
                        found[(BinaryPrimitives.ReadUInt16BigEndian(kern.AsSpan(pair)),
                               BinaryPrimitives.ReadUInt16BigEndian(kern.AsSpan(pair + 2)))]
                            = BinaryPrimitives.ReadInt16BigEndian(kern.AsSpan(pair + 4));
                    }
                    return found;
                }
                at += Math.Max(6, length);
            }
        }
        catch (ArgumentOutOfRangeException)
        {
        }
        return null;
    }
}
