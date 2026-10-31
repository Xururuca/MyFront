namespace MyFront.Core.Db;

/// <summary>Rows shown by a virtual grid.</summary>
public interface IRowSource
{
    int Count { get; }
    object?[] GetRow(int index);
}

/// <summary>
/// Append-only chunked row storage. One background thread appends while the UI thread reads;
/// no locks, no List growth copies of the row data. Supports an optional sort permutation.
/// </summary>
public sealed class RowStore : IRowSource
{
    private const int Shift = 12;
    private const int ChunkSize = 1 << Shift;
    private const int Mask = ChunkSize - 1;

    private object?[][]?[] _chunks = new object?[][]?[16];
    private int _count;
    private int[]? _order;

    public int Count => Volatile.Read(ref _count);

    public void Add(object?[] row)
    {
        int i = _count;
        int c = i >> Shift;
        var chunks = _chunks;
        if (c >= chunks.Length)
        {
            var grown = new object?[][]?[chunks.Length * 2];
            Array.Copy(chunks, grown, chunks.Length);
            Volatile.Write(ref _chunks, grown);
            chunks = grown;
        }
        var chunk = chunks[c] ??= new object?[ChunkSize][];
        chunk[i & Mask] = row;
        Volatile.Write(ref _count, i + 1);
    }

    /// <summary>Row in storage order.</summary>
    public object?[] RawRow(int index) => Volatile.Read(ref _chunks)[index >> Shift]![index & Mask];

    /// <summary>Row in display order (after a sort).</summary>
    public object?[] GetRow(int index)
    {
        var order = _order;
        return RawRow(order is not null && index < order.Length ? order[index] : index);
    }

    /// <summary>Sorts by one column. Call only after loading finished; heavy work, run it off the UI thread.</summary>
    public int[] BuildSortOrder(int column, bool descending)
    {
        int n = Count;
        var order = new int[n];
        var keys = new object?[n];
        for (int i = 0; i < n; i++) { order[i] = i; keys[i] = RawRow(i)[column]; }
        var cmp = ValueComparer.Instance;
        Array.Sort(keys, order, descending ? Comparer<object?>.Create((a, b) => cmp.Compare(b, a)) : cmp);
        return order;
    }

    public void ApplySortOrder(int[]? order) => _order = order;
}

/// <summary>Null-first comparer for mixed DB values; strings compare culture-aware (Turkish ğ/ş/ı sort correctly).</summary>
public sealed class ValueComparer : IComparer<object?>
{
    public static readonly ValueComparer Instance = new();

    public int Compare(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is string sa && b is string sb) return string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase);
        if (a.GetType() == b.GetType() && a is IComparable ca) return ca.CompareTo(b);
        if (IsNumber(a) && IsNumber(b))
        {
            try { return Convert.ToDecimal(a).CompareTo(Convert.ToDecimal(b)); }
            catch (OverflowException) { return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b)); }
        }
        if (a is byte[] ba && b is byte[] bb) return ba.AsSpan().SequenceCompareTo(bb);
        return string.Compare(a.ToString(), b.ToString(), StringComparison.CurrentCultureIgnoreCase);
    }

    private static bool IsNumber(object o) => o is sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal;
}
