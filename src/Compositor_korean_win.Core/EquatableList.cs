using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Compositor_korean_win.Core;

/// <summary>An immutable list that compares by its contents.</summary>
/// <remarks>
/// Swift's arrays are values: two documents holding equal layers are equal. The history layer leans
/// on that — <see cref="DocumentHistory.End"/> decides whether an edit happened at all by comparing
/// the document before and after, and a selection or a navigation that changed nothing must not
/// consume the redo stack. A .NET record holding a plain <c>List&lt;T&gt;</c> would compare the
/// references instead and call every edit a change, so collections inside the document model use
/// this.
///
/// Serialising it needs <see cref="EquatableListConverter{T}"/> named on the property as a closed
/// generic. A converter factory would be shorter, but it would reach for
/// <c>Type.MakeGenericType</c>, which NativeAOT cannot see through.
/// </remarks>
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    private readonly T[] _items;
    private int _hash;

    public EquatableList(IEnumerable<T> items) => _items = items.ToArray();

    public static EquatableList<T> Empty { get; } = new([]);

    public T this[int index] => _items[index];
    public int Count => _items.Length;

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => _items.GetEnumerator();

    /// <summary>The same list with one element replaced.</summary>
    public EquatableList<T> With(int index, T item)
    {
        T[] copy = (T[])_items.Clone();
        copy[index] = item;
        return new EquatableList<T>(copy);
    }

    public bool Equals(EquatableList<T>? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null || other._items.Length != _items.Length) return false;

        var comparer = EqualityComparer<T>.Default;
        for (int i = 0; i < _items.Length; i++)
            if (!comparer.Equals(_items[i], other._items[i])) return false;

        return true;
    }

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        // Cached: history compares documents on every edit, and a document's layers hash through
        // every adjustment they carry. Zero stands for "not computed yet", which at worst costs one
        // recomputation for a list that genuinely hashes to it.
        if (_hash != 0) return _hash;

        var hash = new HashCode();
        foreach (T item in _items) hash.Add(item);
        int value = hash.ToHashCode();
        return _hash = value != 0 ? value : 1;
    }

    public static bool operator ==(EquatableList<T>? left, EquatableList<T>? right) =>
        left is null ? right is null : left.Equals(right);

    public static bool operator !=(EquatableList<T>? left, EquatableList<T>? right) => !(left == right);
}

public static class EquatableListExtensions
{
    public static EquatableList<T> ToEquatableList<T>(this IEnumerable<T> items) => new(items);
}

/// <summary>Reads and writes an <see cref="EquatableList{T}"/> as a plain JSON array.</summary>
public sealed class EquatableListConverter<T> : JsonConverter<EquatableList<T>>
{
    public override EquatableList<T> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("expected an array");

        var items = new List<T>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            items.Add(JsonSerializer.Deserialize<T>(ref reader, options)!);

        return new EquatableList<T>(items);
    }

    public override void Write(Utf8JsonWriter writer, EquatableList<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (T item in value) JsonSerializer.Serialize(writer, item, options);
        writer.WriteEndArray();
    }
}
