namespace MyFront.Core.Db;

/// <summary>
/// Shares repeated cell values (city names, dates, small ints…) between rows of one result,
/// so a million-row result keeps one "İstanbul" string instead of a million copies.
/// A column stops being interned once it shows more distinct values than the cap.
/// </summary>
internal sealed class ValueInterner
{
    private const int MaxDistinct = 4096;
    private const int MaxStringLength = 128;
    private readonly Dictionary<object, object>?[] _maps;

    public ValueInterner(int columns)
    {
        _maps = new Dictionary<object, object>?[columns];
        for (int i = 0; i < columns; i++) _maps[i] = new Dictionary<object, object>();
    }

    public object? Intern(int column, object? value)
    {
        var map = _maps[column];
        if (map is null || value is null || value is byte[] || (value is string s && s.Length > MaxStringLength))
            return value;
        if (map.TryGetValue(value, out var shared)) return shared;
        if (map.Count >= MaxDistinct)
        {
            _maps[column] = null; // too many distinct values: interning would only cost time
            return value;
        }
        map[value] = value;
        return value;
    }
}
