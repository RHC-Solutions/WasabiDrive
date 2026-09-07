namespace WasabiDrive.WinFsp;

/// <summary>
/// A sorted, non-overlapping set of half-open byte ranges — which parts of an open file have been
/// written since it was last uploaded. Adjacent and overlapping ranges are coalesced on insert, so
/// a sequential writer that issues thousands of small writes ends up with one range, not thousands.
/// </summary>
internal sealed class DirtyRanges
{
    private readonly List<(long Start, long End)> _ranges = new();

    public bool IsEmpty => _ranges.Count == 0;

    public int Count => _ranges.Count;

    public long TotalBytes
    {
        get
        {
            long total = 0;
            foreach (var (start, end) in _ranges) total += end - start;
            return total;
        }
    }

    public IReadOnlyList<(long Start, long End)> Ranges => _ranges;

    public void Add(long start, long end)
    {
        if (end <= start) return;

        var index = 0;
        while (index < _ranges.Count && _ranges[index].End < start) index++;

        // Absorb every existing range this one overlaps or abuts.
        while (index < _ranges.Count && _ranges[index].Start <= end)
        {
            start = Math.Min(start, _ranges[index].Start);
            end = Math.Max(end, _ranges[index].End);
            _ranges.RemoveAt(index);
        }

        _ranges.Insert(index, (start, end));
    }

    /// <summary>Drops or trims anything at or beyond <paramref name="limit"/> (a truncation).</summary>
    public void ClipTo(long limit)
    {
        for (var i = _ranges.Count - 1; i >= 0; i--)
        {
            var (start, end) = _ranges[i];
            if (start >= limit) _ranges.RemoveAt(i);
            else if (end > limit) _ranges[i] = (start, limit);
        }
    }

    public void Clear() => _ranges.Clear();

    public DirtyRanges Copy()
    {
        var copy = new DirtyRanges();
        copy._ranges.AddRange(_ranges);
        return copy;
    }
}
