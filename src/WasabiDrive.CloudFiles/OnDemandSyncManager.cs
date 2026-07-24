using System.Text;
using WasabiDrive.Core.Models;

namespace WasabiDrive.CloudFiles;

/// <summary>
/// Orchestrates one bucket's Files On-Demand folder: builds the S3 client and
/// <see cref="CloudFilesProvider"/>, registers/connects the sync root, populates placeholders from
/// the bucket listing, and exposes pin / free-up-space / auto-dehydrate operations.
/// One-way (read) for this milestone.
/// </summary>
public sealed class OnDemandSyncManager : IDisposable
{
    private readonly Mapping _mapping;
    private readonly WasabiS3Client _s3;
    private readonly CloudFilesProvider _provider;
    private readonly Action<string>? _log;
    private LocalChangeSyncer? _syncer;

    public OnDemandSyncManager(Mapping mapping, WasabiCredentials credentials, Action<string>? log = null)
    {
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        _log = log;
        _s3 = WasabiS3Client.ForMapping(mapping, credentials);

        SyncRootPath = ResolveFolderPath(mapping);
        _provider = new CloudFilesProvider(
            SyncRootPath,
            providerId: mapping.Id,
            syncRootIdentity: Encoding.UTF8.GetBytes(mapping.RemoteName),
            openRead: (key, offset, length, ct) => _s3.OpenReadAsync(key, offset, length, ct),
            log: log);
    }

    /// <summary>The local folder that appears in Explorer.</summary>
    public string SyncRootPath { get; }

    /// <summary>Default folder: %USERPROFILE%\WasabiDrive\&lt;name&gt; when none is configured.</summary>
    public static string ResolveFolderPath(Mapping mapping)
    {
        if (!string.IsNullOrWhiteSpace(mapping.LocalFolderPath))
            return mapping.LocalFolderPath!;
        var leaf = string.IsNullOrWhiteSpace(mapping.Name) ? mapping.BucketName : mapping.Name;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "WasabiDrive", Sanitize(leaf));
    }

    /// <summary>
    /// Registers + connects the sync root, populates placeholders from the bucket, and starts
    /// watching for local changes to push back up (two-way).
    /// </summary>
    public async Task EnableAsync(CancellationToken ct = default)
    {
        _provider.Register();
        ApplyBranding();
        _provider.Connect();
        await PopulateAsync(ct).ConfigureAwait(false);

        // Start the watcher after population so the initial placeholders don't look like new files.
        _syncer = new LocalChangeSyncer(SyncRootPath, NormalizePrefix(_mapping.SubPath), _s3, _provider, _log);
        _syncer.Start();
    }

    /// <summary>Disconnects hydration and local-change syncing but leaves the folder in place.</summary>
    public void Disable()
    {
        _syncer?.Dispose();
        _syncer = null;
        _provider.Disconnect();
    }

    /// <summary>Disconnects and removes the sync-root registration (keeps local files).</summary>
    public void Unregister() => _provider.Unregister();

    public void Pin(string fullPath) => _provider.SetPinned(fullPath, pinned: true);
    public void Unpin(string fullPath) => _provider.SetPinned(fullPath, pinned: false);
    public void FreeUpSpace(string fullPath) => _provider.Dehydrate(fullPath);

    /// <summary>
    /// Populates the local namespace with placeholders for every object in the bucket (under the
    /// mapping's optional sub-path). Existing entries are skipped, so it is safe to re-run.
    /// </summary>
    public async Task PopulateAsync(CancellationToken ct = default)
    {
        var prefix = NormalizePrefix(_mapping.SubPath);
        var byDirectory = new Dictionary<string, List<PlaceholderInfo>>(StringComparer.OrdinalIgnoreCase);

        await foreach (var obj in _s3.ListObjectsAsync(prefix, ct).ConfigureAwait(false))
        {
            if (obj.Key.EndsWith('/')) continue; // folder marker

            var relativeKey = prefix.Length > 0 && obj.Key.StartsWith(prefix, StringComparison.Ordinal)
                ? obj.Key[prefix.Length..]
                : obj.Key;
            relativeKey = relativeKey.TrimStart('/');
            if (relativeKey.Length == 0) continue;

            var segments = relativeKey.Split('/');
            var fileName = segments[^1];
            var relativeDir = segments.Length > 1
                ? string.Join(Path.DirectorySeparatorChar, segments[..^1])
                : string.Empty;

            var fullPath = Path.Combine(SyncRootPath,
                relativeDir.Length == 0 ? fileName : Path.Combine(relativeDir, fileName));
            if (File.Exists(fullPath)) continue; // already created/hydrated

            if (!byDirectory.TryGetValue(relativeDir, out var list))
                byDirectory[relativeDir] = list = new List<PlaceholderInfo>();
            list.Add(new PlaceholderInfo(fileName, obj.Key, obj.Size, obj.LastModifiedUtc));
        }

        var created = 0;
        foreach (var (dir, files) in byDirectory)
        {
            try { created += _provider.CreatePlaceholders(dir, files); }
            catch (Exception ex) { _log?.Invoke($"Placeholder creation in '{dir}' failed: {ex.Message}"); }
        }
        _log?.Invoke($"On-demand folder '{SyncRootPath}': {created} placeholder(s) created.");
    }

    /// <summary>
    /// "Return to cloud after some time": dehydrates hydrated, unpinned files whose last access is
    /// older than <paramref name="idleFor"/>. Windows Storage Sense can also do this automatically
    /// (we enable that policy), but this gives us an explicit fallback.
    /// </summary>
    public int DehydrateIdle(TimeSpan idleFor)
    {
        if (!Directory.Exists(SyncRootPath)) return 0;
        var cutoff = DateTime.Now - idleFor;
        var dehydrated = 0;

        foreach (var file in Directory.EnumerateFiles(SyncRootPath, "*", SearchOption.AllDirectories))
        {
            try
            {
                var info = new FileInfo(file);
                // A cloud-only placeholder carries RECALL_ON_DATA_ACCESS — already dehydrated, skip.
                if (((int)info.Attributes & FileAttributeRecallOnDataAccess) != 0) continue;
                // Pinned files carry the PINNED attribute; leave them on disk.
                if (((int)info.Attributes & FileAttributePinned) != 0) continue;
                if (info.LastAccessTime > cutoff) continue;
                _provider.Dehydrate(file);
                dehydrated++;
            }
            catch (Exception ex) { _log?.Invoke($"Dehydrate '{file}' failed: {ex.Message}"); }
        }
        if (dehydrated > 0) _log?.Invoke($"Auto-dehydrated {dehydrated} idle file(s) in '{SyncRootPath}'.");
        return dehydrated;
    }

    // Cloud Files placeholder attributes (winnt.h). Not all are in the .NET FileAttributes enum.
    private const int FileAttributeRecallOnDataAccess = 0x00400000;
    private const int FileAttributePinned = 0x00080000;

    private void ApplyBranding()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(exe))
            FolderBranding.Apply(SyncRootPath, exe + ",0",
                tooltip: $"WasabiDrive — {_mapping.BucketName} (Files On-Demand)");
    }

    private static string NormalizePrefix(string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath)) return string.Empty;
        var p = subPath.Trim().Trim('/');
        return p.Length == 0 ? string.Empty : p + "/";
    }

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    public void Dispose()
    {
        _syncer?.Dispose();
        _provider.Dispose();
        _s3.Dispose();
    }
}
