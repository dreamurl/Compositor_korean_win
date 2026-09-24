using System.Text;

namespace Compositor_korean_win.Core;

/// <summary>A value with a unit, such as <c>#Pxl</c> pixels, <c>#Prc</c> percent or <c>#Ang</c> degrees.</summary>
internal sealed record PsdUnitFloat(string Unit, double Value);

/// <summary>An enumerated value: its type and the value within it.</summary>
internal sealed record PsdEnum(string Type, string Value);

/// <summary>
/// Photoshop's action descriptor: a class and a list of keyed, typed values, nested freely.
/// </summary>
/// <remarks>
/// Layer effects, fills and much else newer than the fixed-layout blocks are stored this way. Only
/// what this port reads and writes is modelled; a reference (<c>obj </c>) or object array is read
/// past but not kept, since neither turns up in the blocks that are used.
/// </remarks>
internal sealed class PsdDescriptor
{
    public string Name { get; init; } = "";
    public string ClassId { get; init; } = "null";
    public List<(string Key, object Value)> Items { get; } = [];

    public object? this[string key] => Items.FirstOrDefault(item => item.Key == key).Value;

    public PsdDescriptor Add(string key, object value)
    {
        Items.Add((key, value));
        return this;
    }

    public double? Number(string key) => this[key] switch
    {
        double value => value,
        PsdUnitFloat unit => unit.Value,
        int value => value,
        long value => value,
        _ => null,
    };

    public bool? Bool(string key) => this[key] as bool?;
    public string? Enum(string key) => (this[key] as PsdEnum)?.Value;
    public PsdDescriptor? Object(string key) => this[key] as PsdDescriptor;
    public List<object>? List(string key) => this[key] as List<object>;

    public static PsdDescriptor Read(PsdReader reader)
    {
        string name = reader.Unicode();
        string classId = Id(reader);
        var descriptor = new PsdDescriptor { Name = name, ClassId = classId };
        uint count = reader.U32();
        for (uint i = 0; i < count; i++)
        {
            string key = Id(reader);
            descriptor.Items.Add((key, Value(reader, reader.Key())));
        }
        return descriptor;
    }

    private static string Id(PsdReader reader)
    {
        uint length = reader.U32();
        if (length == 0) return reader.Key();
        if (length > (uint)reader.Remaining) throw PsdFormat.Truncated();
        return Encoding.Latin1.GetString(reader.Bytes((int)length));
    }

    private static object Value(PsdReader reader, string type)
    {
        switch (type)
        {
            case "Objc":
            case "GlbO":
                return Read(reader);
            case "VlLs":
            {
                uint count = reader.U32();
                var list = new List<object>();
                for (uint i = 0; i < count; i++) list.Add(Value(reader, reader.Key()));
                return list;
            }
            case "doub":
                return reader.F64();
            case "UntF":
                return new PsdUnitFloat(reader.Key(), reader.F64());
            case "UnFl":
            {
                string unit = reader.Key();
                uint count = reader.U32();
                var values = new List<object>();
                for (uint i = 0; i < count; i++) values.Add(new PsdUnitFloat(unit, reader.F64()));
                return values;
            }
            case "TEXT":
                return reader.Unicode();
            case "enum":
                return new PsdEnum(Id(reader), Id(reader));
            case "long":
                return reader.I32();
            case "comp":
                return reader.I64();
            case "bool":
                return reader.U8() != 0;
            case "type":
            case "GlbC":
                reader.Unicode();
                return Id(reader);
            case "alis":
            case "tdta":
            case "Pth ":
            {
                uint length = reader.U32();
                if (length > (uint)reader.Remaining) throw PsdFormat.Truncated();
                return reader.Bytes((int)length).ToArray();
            }
            case "obj ":
                SkipReference(reader);
                return "reference";
            default:
                throw PsdFormat.Invalid($"descriptor value of type '{type}'");
        }
    }

    private static void SkipReference(PsdReader reader)
    {
        uint count = reader.U32();
        for (uint i = 0; i < count; i++)
        {
            switch (reader.Key())
            {
                case "prop":
                    reader.Unicode(); Id(reader); Id(reader);
                    break;
                case "Clss":
                    reader.Unicode(); Id(reader);
                    break;
                case "Enmr":
                    reader.Unicode(); Id(reader); Id(reader); Id(reader);
                    break;
                case "rele":
                    reader.Unicode(); Id(reader); reader.U32();
                    break;
                case "Idnt":
                case "indx":
                    reader.U32();
                    break;
                case "name":
                    reader.Unicode(); Id(reader); reader.Unicode();
                    break;
                default:
                    throw PsdFormat.Invalid("descriptor reference");
            }
        }
    }

    public void Write(PsdWriter writer)
    {
        writer.Unicode(Name, terminated: true);
        WriteId(writer, ClassId);
        writer.U32((uint)Items.Count);
        foreach ((string key, object value) in Items)
        {
            WriteId(writer, key);
            WriteValue(writer, value);
        }
    }

    private static void WriteId(PsdWriter writer, string id)
    {
        if (id.Length == 4)
        {
            writer.U32(0);
            writer.Key(id);
            return;
        }
        writer.U32((uint)id.Length);
        writer.Bytes(Encoding.Latin1.GetBytes(id));
    }

    private static void WriteValue(PsdWriter writer, object value)
    {
        switch (value)
        {
            case PsdDescriptor descriptor:
                writer.Key("Objc");
                descriptor.Write(writer);
                break;
            case List<object> list:
                writer.Key("VlLs");
                writer.U32((uint)list.Count);
                foreach (object item in list) WriteValue(writer, item);
                break;
            case double number:
                writer.Key("doub");
                writer.F64(number);
                break;
            case PsdUnitFloat unit:
                writer.Key("UntF");
                writer.Key(unit.Unit);
                writer.F64(unit.Value);
                break;
            case string text:
                writer.Key("TEXT");
                writer.Unicode(text, terminated: true);
                break;
            case PsdEnum enumerated:
                writer.Key("enum");
                WriteId(writer, enumerated.Type);
                WriteId(writer, enumerated.Value);
                break;
            case int integer:
                writer.Key("long");
                writer.I32(integer);
                break;
            case bool flag:
                writer.Key("bool");
                writer.U8(flag ? (byte)1 : (byte)0);
                break;
            case byte[] data:
                writer.Key("tdta");
                writer.U32((uint)data.Length);
                writer.Bytes(data);
                break;
            default:
                throw new ArgumentException($"a descriptor cannot hold a {value.GetType().Name}", nameof(value));
        }
    }
}
