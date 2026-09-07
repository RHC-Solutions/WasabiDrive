using System.Collections.Concurrent;
using WasabiDrive.CloudFiles;

namespace WasabiDrive.WinFsp;

/// <summary>One entry in a directory listing.</summary>
internal sealed record S3Node(
    string Name, bool IsDirectory, long Size, DateTime LastModifiedUtc, string? ETag);

/// <summary>
/// Directory-listing cache with a TTL — the equivalent of rclone's <c>--dir-cache-time</c>. S3 has
/// no directory index and cannot push change notifications, so every path lookup is answered from
/// a cached single-level (delimited) listing of the parent directory. Looking a file up with HEAD
/// instead would cost one request per Explorer glance.
///
/// The cost this shares with rclone: a directory whose listing takes longer than the TTL can never
/// be served, because the entry expires before it is complete. That is what makes a bucket with a
/// huge flat root unusable at a 1-minute TTL.
/// </summary>
internal sealed class MetadataCache
{
    private sealed class DirEntry
    {
        public readonly object Gate = new();
        public Dictionary<string, S3Node> Children = new(StringComparer.OrdinalIgnoreCase);
        public DateTime LoadedUtc = DateTime.MinValue;
        public bool Loaded;
    }

    private readonly WasabiS3Client _s3;
    private readonly S3Paths _paths;
    private readonly TimeSpan _ttl;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<string, DirEntry> _dirs =
        new(StringComparer.OrdinalIgnoreCase);

    public MetadataCache(WasabiS3Client s3, S3Paths paths, TimeSpan ttl, Action<string>? log)
    {
        _s3 = s3;
        _paths = paths;
        _ttl = ttl <= TimeSpan.Zero ? TimeSpan.FromMinutes(1) : ttl;
        _log = log;
    }

    /// <summary>Children of a directory, listed from S3 on a miss or expiry.</summary>
    public IReadOnlyDictionary<string, S3Node> GetChildren(string? dirPath)
    {
        var key = S3Paths.Normalize(dirPath);
        var entry = _dirs.GetOrAdd(key, _ => new DirEntry());

        lock (entry.Gate)
        {
            if (entry.Loaded && DateTime.UtcNow - entry.LoadedUtc < _ttl)
                return entry.Children;

            var children = Load(key);
            entry.Children = children;
            entry.LoadedUtc = DateTime.UtcNow;
            entry.Loaded = true;
            return children;
        }
    }

    private Dictionary<string, S3Node> Load(string dirPath)
    {
        var prefix = _paths.ToDirPrefix(dirPath);
        var result = new Dictionary<string, S3Node>(StringComparer.OrdinalIgnoreCase);
        var started = DateTime.UtcNow;

        var enumerator = _s3.ListDirectoryAsync(prefix).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var page = enumerator.Current;

                foreach (var file in page.Files)
                {
                    // A key ending in "/" is a directory marker, not a file.
                    if (file.Key.EndsWith('/')) continue;
                    if (file.Key.Length <= prefix.Length) continue;

                    var name = file.Key[prefix.Length..];
                    if (name.Length == 0 || name.Contains('/')) continue;
                    result[name] = new S3Node(name, false, file.Size, file.LastModifiedUtc, file.ETag);
                }

                foreach (var sub in page.SubPrefixes)
                {
                    if (sub.Length <= prefix.Length) continue;
                    var name = sub[prefix.Length..].TrimEnd('/');
                    if (name.Length == 0) continue;
                    result[name] = new S3Node(name, true, 0, DateTime.UtcNow, null);
                }
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        var elapsed = DateTime.UtcNow - started;
        if (elapsed > _ttl)
        {
            _log?.Invoke(
                $"WARNING: listing '{prefix}' took {elapsed.TotalSeconds:N0}s, longer than the " +
                $"{_ttl.TotalSeconds:N0}s dir-cache TTL. Raise Dir cache time or this directory " +
                "will never finish opening.");
        }
        else if (elapsed > TimeSpan.FromSeconds(5))
        {
            _log?.Invoke($"Listed '{prefix}': {result.Count} entries in {elapsed.TotalSeconds:N1}s");
        }

        return result;
    }

    /// <summary>Metadata for one path, or null when it does not exist. The root is always a directory.</summary>
    public S3Node? Lookup(string? path)
    {
        var p = S3Paths.Normalize(path);
        if (S3Paths.IsRoot(p)) return new S3Node(string.Empty, true, 0, DateTime.UtcNow, null);

        var children = GetChildren(S3Paths.Parent(p));
        return children.TryGetValue(S3Paths.Leaf(p), out var node) ? node : null;
    }

    public bool Exists(string? path) => Lookup(path) is not null;

    /// <summary>Forces the next lookup in this directory to re-list.</summary>
    public void Invalidate(string? dirPath) => _dirs.TryRemove(S3Paths.Normalize(dirPath), out _);

    /// <summary>Drops the whole cache — used after an operation rewrites a subtree.</summary>
    public void InvalidateAll() => _dirs.Clear();

    /// <summary>
    /// Records a locally-made change in the parent's cached listing so it shows up immediately
    /// instead of after the TTL. Keeps Explorer honest right after a create, write or delete.
    /// </summary>
    public void Upsert(string? path, S3Node node)
    {
        if (_dirs.TryGetValue(S3Paths.Normalize(S3Paths.Parent(path)), out var entry))
        {
            lock (entry.Gate)
            {
                if (entry.Loaded) entry.Children[node.Name] = node;
            }
        }
    }

    public void Remove(string? path)
    {
        if (_dirs.TryGetValue(S3Paths.Normalize(S3Paths.Parent(path)), out var entry))
        {
            lock (entry.Gate)
            {
                if (entry.Loaded) entry.Children.Remove(S3Paths.Leaf(path));
            }
        }
        _dirs.TryRemove(S3Paths.Normalize(path), out _);
    }
}
