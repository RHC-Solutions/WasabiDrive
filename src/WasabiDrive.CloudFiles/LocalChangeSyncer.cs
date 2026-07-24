using System.Collections.Concurrent;
using WasabiDrive.Core.Sync;

namespace WasabiDrive.CloudFiles;

/// <summary>
/// Watches an on-demand folder and pushes local changes up to Wasabi: new/edited files are
/// uploaded and marked in-sync, deletes remove the object (cascading for folders), and renames
/// become server-side moves (cascading for folders). A persisted <see cref="SyncStateStore"/> lets
/// it skip redundant uploads and recognise hydration/self writes so they don't cause upload loops.
/// </summary>
internal sealed class LocalChangeSyncer : IDisposable
{
    private const int DebounceMs = 1500;
    private const int IgnoreSeconds = 20;

    private readonly string _syncRoot;
    private readonly string _prefix;
    private readonly WasabiS3Client _s3;
    private readonly CloudFilesProvider _provider;
    private readonly SyncStateStore _state;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<string, DateTime> _ignoreUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;

    public LocalChangeSyncer(string syncRoot, string prefix, WasabiS3Client s3,
        CloudFilesProvider provider, SyncStateStore state, Action<string>? log)
    {
        _syncRoot = syncRoot;
        _prefix = prefix;
        _s3 = s3;
        _provider = provider;
        _state = state;
        _log = log;
        _debounce = new Timer(_ => ProcessPending(), null, Timeout.Infinite, Timeout.Infinite);
        _provider.Hydrated += OnHydrated;
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_syncRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                         | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        _watcher.Created += (_, e) => QueueChange(e.FullPath);
        _watcher.Changed += (_, e) => QueueChange(e.FullPath);
        _watcher.Deleted += OnDeleted;
        _watcher.Renamed += OnRenamed;
        _watcher.EnableRaisingEvents = true;
        _log?.Invoke($"Two-way sync watching {_syncRoot}");
    }

    /// <summary>After hydration, record the file's state so it isn't mistaken for a local edit.</summary>
    private void OnHydrated(string key)
    {
        var path = LocalPathForKey(key);
        Ignore(path);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return;
            var known = _state.Get(key);
            _state.Set(new SyncEntry
            {
                Key = key,
                ETag = known?.ETag,
                Size = info.Length,
                LocalModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                RemoteModifiedUtcTicks = known?.RemoteModifiedUtcTicks ?? 0,
            });
        }
        catch { /* best-effort */ }
    }

    private void Ignore(string path) => _ignoreUntil[path] = DateTime.UtcNow.AddSeconds(IgnoreSeconds);

    private bool IsIgnored(string path) =>
        _ignoreUntil.TryGetValue(path, out var until) && DateTime.UtcNow < until;

    private void QueueChange(string path)
    {
        if (Directory.Exists(path)) return;
        _pending[path] = 1;
        _debounce.Change(DebounceMs, Timeout.Infinite);
    }

    private void ProcessPending()
    {
        var paths = _pending.Keys.ToArray();
        foreach (var p in paths) _pending.TryRemove(p, out _);
        foreach (var path in paths)
            _ = Task.Run(() => UploadIfNeededAsync(path));
    }

    private async Task UploadIfNeededAsync(string path)
    {
        try
        {
            if (!File.Exists(path) || IsIgnored(path)) return;
            if (CloudFilesProvider.IsDehydrated(path)) return; // cloud-only placeholder, nothing local to push
            if (IsLocked(path)) { QueueChange(path); return; }

            var info = new FileInfo(path);
            var key = KeyForLocalPath(path);
            var known = _state.Get(key);

            var action = SyncReconciler.DecideUpload(
                info.Length, info.LastWriteTimeUtc.Ticks, known, remoteChangedSinceSync: false);
            if (action == UploadAction.Skip) return;

            Ignore(path); // suppress the writes our own in-sync marking will cause
            string? etag = null;
            await S3Retry.RunAsync(async () => etag = await _s3.PutObjectAsync(key, path).ConfigureAwait(false), _log)
                .ConfigureAwait(false);

            try { _provider.MarkInSync(path); }
            catch { try { _provider.ConvertToPlaceholder(path, key); } catch { /* leave as normal file */ } }

            _state.Set(new SyncEntry
            {
                Key = key,
                ETag = etag,
                Size = info.Length,
                LocalModifiedUtcTicks = info.LastWriteTimeUtc.Ticks,
                RemoteModifiedUtcTicks = DateTime.UtcNow.Ticks,
            });
            _log?.Invoke($"Uploaded {key}");
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Upload of '{path}' failed: {ex.Message}");
        }
    }

    private void OnDeleted(object sender, FileSystemEventArgs e)
    {
        var path = e.FullPath;
        if (IsIgnored(path)) return;
        var key = KeyForLocalPath(path);
        // The path is already gone, so we can't tell file vs directory — handle both: delete the
        // exact object and every tracked object beneath it (folder cascade).
        var childPrefix = key + "/";
        var keysToDelete = new List<string> { key };
        keysToDelete.AddRange(_state.Keys.Where(k => k.StartsWith(childPrefix, StringComparison.Ordinal)));

        _ = Task.Run(async () =>
        {
            foreach (var k in keysToDelete.Distinct())
            {
                try
                {
                    await S3Retry.RunAsync(() => _s3.DeleteObjectAsync(k), _log).ConfigureAwait(false);
                    _state.Remove(k);
                    _log?.Invoke($"Deleted {k}");
                }
                catch (Exception ex) { _log?.Invoke($"Delete of '{k}' failed: {ex.Message}"); }
            }
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        var oldKey = KeyForLocalPath(e.OldFullPath);
        var newKey = KeyForLocalPath(e.FullPath);
        var isDirectory = Directory.Exists(e.FullPath);

        var moves = new List<(string From, string To)>();
        if (!isDirectory) moves.Add((oldKey, newKey));
        var oldPrefix = oldKey + "/";
        var newPrefix = newKey + "/";
        foreach (var k in _state.Keys.Where(k => k.StartsWith(oldPrefix, StringComparison.Ordinal)))
            moves.Add((k, newPrefix + k[oldPrefix.Length..]));

        _ = Task.Run(async () =>
        {
            foreach (var (from, to) in moves)
            {
                try
                {
                    await S3Retry.RunAsync(() => _s3.MoveObjectAsync(from, to), _log).ConfigureAwait(false);
                    var known = _state.Get(from);
                    _state.Remove(from);
                    if (known is not null) { known.Key = to; _state.Set(known); }
                    _log?.Invoke($"Renamed {from} -> {to}");
                }
                catch (Exception ex) { _log?.Invoke($"Rename '{from}' failed: {ex.Message}"); }
            }
        });
    }

    private string KeyForLocalPath(string path)
    {
        var relative = Path.GetRelativePath(_syncRoot, path).Replace(Path.DirectorySeparatorChar, '/');
        return _prefix + relative;
    }

    private string LocalPathForKey(string key)
    {
        var relative = _prefix.Length > 0 && key.StartsWith(_prefix, StringComparison.Ordinal)
            ? key[_prefix.Length..]
            : key;
        return Path.Combine(_syncRoot, relative.Replace('/', Path.DirectorySeparatorChar));
    }

    private static bool IsLocked(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (IOException) { return true; }
        catch { return false; }
    }

    public void Dispose()
    {
        _provider.Hydrated -= OnHydrated;
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
        _debounce.Dispose();
    }
}
