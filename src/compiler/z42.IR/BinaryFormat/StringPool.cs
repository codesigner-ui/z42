namespace Z42.IR.BinaryFormat;

/// <summary>
/// Interning string pool used during binary serialization.
/// Maintains insertion order and O(1) lookup by value.
/// </summary>
public sealed class StringPool
{
    private readonly Dictionary<string, int> _index = new(StringComparer.Ordinal);
    private readonly List<string>            _list  = [];

    public int Intern(string s)
    {
        if (!_index.TryGetValue(s, out int idx))
        {
            idx = _list.Count;
            _index[s] = idx;
            _list.Add(s);
        }
        return idx;
    }

    /// Returns the index of a previously interned string; throws if not found.
    public int Idx(string s) => _index[s];

    public IReadOnlyList<string> AllStrings => _list;
    public int Count => _list.Count;
}
