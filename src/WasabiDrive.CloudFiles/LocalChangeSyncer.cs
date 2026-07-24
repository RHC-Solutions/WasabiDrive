using System.Collections.Concurrent;

namespace WasabiDrive.CloudFiles;

/// <summary>
/// Watches an on-demand folder and pushes local changes up to Wasabi: new/edited files are
/// uploaded and marked in-sync, deletes remove the object, and renames become a server-side move.
/// Writes caused by hydration (Windows filling a placeholder) and by our own in-sync marking are
/// ignored so they don't cause upload loops.
/// </summary>
internal sealed class LocalChangeSyncer : IDisposable
{
    private const int DebounceMs = 1500;
    private const int IgnoreSeconds = 20;

    private readonly string _syncRoot;
    private readonly string _prefix;
    private readonly WasabiS3Client _s3;
    private readonly CloudFilesProvider _provider;
    private readonly Action<string>? _log;

    private readonly ConcurrentDictionary<string, DateTime> _ignoreUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounce;
    private FileSystemWatcher? _watcher;

    public LocalChangeSyncer(string syncRoot, string prefix, WasabiS3Client s3, CloudFilesProvider provider, Action<string>? log)
    {
        _syncRoot = syncRoot;
        _prefix = prefix;
        _s3 = s3;
        _provider = provider;
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

    private void OnHydrated(string key) => Ignore(LocalPathForKey(key));

    private void Ignore(string path) => _ignoreUntil[path] = DateTime.UtcNow.AddSeconds(IgnoreSeconds);

    private bool IsIgnored(string path) =>
        _ignoreUntil.TryGetValue(path, out var until) && DateTime.UtcNow < until;

    private void QueueChange(string path)
    {
        if (Directory.Exists(path)) return; // directories are created implicitly by object keys
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
            if (IsLocked(path)) { QueueChange(path); return; } // still being written; try again shortly

            var key = KeyForLocalPath(path);
            Ignore(path); // suppress the writes our own in-sync marking will cause
            await _s3.PutObjectAsync(key, path).ConfigureAwait(false);

            // Mark it in-sync; if it was a brand-new normal file, convert it into a placeholder.
            try { _provider.MarkInSync(path); }
            catch { try { _provider.ConvertToPlaceholder(path, key); } catch { /* leave as normal file */ } }

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
        _ = Task.Run(async () =>
        {
            try { await _s3.DeleteObjectAsync(key).ConfigureAwait(false); _log?.Invoke($"Deleted {key}"); }
            catch (Exception ex) { _log?.Invoke($"Delete of '{key}' failed: {ex.Message}"); }
        });
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (Directory.Exists(e.FullPath)) return; // directory renames aren't reconciled in this milestone
        var oldKey = KeyForLocalPath(e.OldFullPath);
        var newKey = KeyForLocalPath(e.FullPath);
        _ = Task.Run(async () =>
        {
            try { await _s3.MoveObjectAsync(oldKey, newKey).ConfigureAwait(false); _log?.Invoke($"Renamed {oldKey} -> {newKey}"); }
            catch (Exception ex) { _log?.Invoke($"Rename '{oldKey}' failed: {ex.Message}"); }
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
