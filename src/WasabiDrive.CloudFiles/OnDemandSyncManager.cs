using System.Text;
using WasabiDrive.Core.Models;
using WasabiDrive.Core.Sync;

namespace WasabiDrive.CloudFiles;

/// <summary>
/// Orchestrates one bucket's Files On-Demand folder: builds the S3 client and
/// <see cref="CloudFilesProvider"/>, registers/connects the sync root, serves directory listings on
/// demand, pushes local changes back up (two-way), periodically pulls remote changes, and exposes
/// pin / free-up-space / auto-dehydrate operations.
///
/// Nothing here ever enumerates the whole bucket. Enabling the folder lists one level; each folder
/// the user opens lists one more, and folders nobody opens are never listed at all. That is what
/// keeps memory proportional to what is on screen rather than to the object count — a bucket whose
/// root holds hundreds of thousands of keys used to have to be walked in full, into a dictionary,
/// before a single placeholder appeared.
/// </summary>
public sealed class OnDemandSyncManager : IDisposable
{
    /// <summary>
    /// Ceiling on the number of entries returned for a single directory. A folder this large is
    /// unusable in Explorer regardless of who is serving it, and the listing has to be held in one
    /// array to be handed to cfapi — so this is the one place where a pathological bucket layout
    /// could still cost real memory. Hitting it is logged rather than passed over quietly.
    /// </summary>
    private const int MaxEntriesPerDirectory = 100_000;

    private readonly Mapping _mapping;
    private readonly string _prefix;
    private readonly WasabiS3Client _s3;
    private readonly CloudFilesProvider _provider;
    private readonly SyncStateStore _state;
    private readonly Action<string>? _log;
    private LocalChangeSyncer? _syncer;
    private Timer? _remotePull;

    public OnDemandSyncManager(Mapping mapping, WasabiCredentials credentials, Action<string>? log = null)
    {
        _mapping = mapping ?? throw new ArgumentNullException(nameof(mapping));
        _log = log;
        _prefix = NormalizePrefix(mapping.SubPath);
        _s3 = WasabiS3Client.ForMapping(mapping, credentials);
        _state = new SyncStateStore(SyncStateStore.FilePathFor(mapping.Id));

        SyncRootPath = ResolveFolderPath(mapping);
        _provider = new CloudFilesProvider(
            SyncRootPath,
            providerId: mapping.Id,
            syncRootIdentity: Encoding.UTF8.GetBytes(mapping.RemoteName),
            openRead: (key, offset, length, ct) => _s3.OpenReadAsync(key, offset, length, ct),
            log: log)
        {
            RootPrefix = _prefix,
            // Windows calls this the first time anything enumerates a folder we left empty.
            DirectorySource = ListDirectoryEntriesAsync,
        };
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
    /// Registers + connects the sync root, fills in its top level, and starts watching for local
    /// changes to push back up (two-way).
    /// </summary>
    public async Task EnableAsync(CancellationToken ct = default)
    {
        _provider.Register();
        ApplyBranding();
        _provider.Connect();
        await PopulateAsync(ct).ConfigureAwait(false);

        // Start the watcher after population so the initial placeholders don't look like new files.
        _syncer = new LocalChangeSyncer(SyncRootPath, _prefix, _s3, _provider, _state, _log);
        _syncer.Start();

        // Periodically pull remote changes (new/updated/deleted objects) into the folder.
        _remotePull = new Timer(_ => _ = ReconcileRemoteAsync(), null,
            TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
    }

    /// <summary>Disconnects hydration and local-change syncing but leaves the folder in place.</summary>
    public void Disable()
    {
        _remotePull?.Dispose();
        _remotePull = null;
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
    /// Fills in the folder's top level only. Sub-folders appear as empty directory placeholders and
    /// list themselves the first time they are opened.
    /// </summary>
    public async Task PopulateAsync(CancellationToken ct = default)
    {
        var entries = await ListDirectoryEntriesAsync(_prefix, ct).ConfigureAwait(false);

        var created = 0;
        try { created = _provider.CreatePlaceholders(string.Empty, entries); }
        catch (Exception ex) { _log?.Invoke($"Populating the folder root failed: {ex.Message}"); }

        var folders = entries.Count(e => e.IsDirectory);
        _log?.Invoke(
            $"On-demand folder '{SyncRootPath}': {created} top-level entr(ies) " +
            $"({folders} folder(s) will list themselves when opened).");
    }

    /// <summary>
    /// Lists exactly one directory level and turns it into placeholder entries: sub-prefixes become
    /// directory placeholders, objects become cloud-only files. Also records the objects' remote
    /// state so a later reconcile can tell what changed.
    ///
    /// This is the callback behind the on-demand population, and it is also what
    /// <see cref="PopulateAsync"/> uses for the root — one code path for both.
    /// </summary>
    private async Task<IReadOnlyList<PlaceholderInfo>> ListDirectoryEntriesAsync(
        string prefix, CancellationToken ct)
    {
        var entries = new List<PlaceholderInfo>();
        var states = new List<SyncEntry>();
        var truncated = false;
        // S3 has no folder timestamps, and a placeholder needs one, so folders are stamped with the
        // moment they were listed.
        var now = DateTime.UtcNow;

        await foreach (var page in _s3.ListDirectoryAsync(prefix, ct).ConfigureAwait(false))
        {
            foreach (var sub in page.SubPrefixes)
            {
                var name = LeafName(sub.TrimEnd('/'));
                if (name.Length == 0) continue;
                entries.Add(new PlaceholderInfo(name, sub, 0, now, IsDirectory: true));
            }

            foreach (var obj in page.Files)
            {
                if (obj.Key.EndsWith('/')) continue; // an explicit folder marker, not a file
                var name = LeafName(obj.Key);
                if (name.Length == 0) continue;
                entries.Add(new PlaceholderInfo(name, obj.Key, obj.Size, obj.LastModifiedUtc));
                states.Add(new SyncEntry
                {
                    Key = obj.Key,
                    ETag = obj.ETag,
                    Size = obj.Size,
                    RemoteModifiedUtcTicks = obj.LastModifiedUtc.Ticks,
                });
            }

            if (entries.Count >= MaxEntriesPerDirectory) { truncated = true; break; }
        }

        // One transaction for the whole folder rather than one commit per object.
        if (states.Count > 0) _state.SetMany(states);
        _state.MarkDirectoryListed(prefix, now);

        if (truncated)
            _log?.Invoke(
                $"Folder '{Describe(prefix)}' has more than {MaxEntriesPerDirectory:N0} entries; " +
                "showing the first ones only. Give the mapping a sub-path to narrow it down.");
        else
            _log?.Invoke(
                $"Listed '{Describe(prefix)}': {states.Count} file(s), " +
                $"{entries.Count - states.Count} folder(s).");

        return entries;
    }

    /// <summary>
    /// Pulls remote changes into the folder: new objects become placeholders, objects deleted
    /// remotely have their (clean, cloud-only) placeholders removed, and remote updates refresh
    /// cloud-only placeholders. Hydrated or locally-changed files are never overwritten — those are
    /// logged as conflicts (local wins).
    ///
    /// Only folders that have actually been listed are revisited. Everything else has never been
    /// shown to anyone, so there is nothing there to bring up to date.
    /// </summary>
    public async Task ReconcileRemoteAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var prefix in _state.ListedDirectories())
            {
                ct.ThrowIfCancellationRequested();

                var relativeDir = RelativeDirFor(prefix);
                if (relativeDir is null) continue; // outside this mapping's sub-path

                var localDir = relativeDir.Length == 0
                    ? SyncRootPath
                    : Path.Combine(SyncRootPath, relativeDir);

                // The user deleted the folder locally; stop reconciling a prefix with nowhere to go.
                if (!Directory.Exists(localDir))
                {
                    _state.ForgetDirectoryTree(prefix);
                    continue;
                }

                await ReconcileDirectoryAsync(prefix, relativeDir, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _log?.Invoke($"Remote reconcile failed: {ex.Message}");
        }
    }

    /// <summary>Reconciles one already-listed directory level against the bucket.</summary>
    private async Task ReconcileDirectoryAsync(string prefix, string relativeDir, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        await foreach (var page in _s3.ListDirectoryAsync(prefix, ct).ConfigureAwait(false))
        {
            // Sub-folders that have appeared remotely: add the empty directory placeholder so they
            // show up. Its contents still wait to be opened.
            foreach (var sub in page.SubPrefixes)
            {
                var name = LeafName(sub.TrimEnd('/'));
                if (name.Length == 0) continue;
                var subPath = Path.Combine(relativeDir.Length == 0 ? SyncRootPath
                    : Path.Combine(SyncRootPath, relativeDir), name);
                if (Directory.Exists(subPath)) continue;
                TryCreate(relativeDir,
                    new PlaceholderInfo(name, sub, 0, DateTime.UtcNow, IsDirectory: true), sub);
            }

            foreach (var obj in page.Files)
            {
                if (obj.Key.EndsWith('/')) continue;
                var fileName = LeafName(obj.Key);
                if (fileName.Length == 0) continue;
                seen.Add(obj.Key);

                var fullPath = LocalPathForRelative(relativeDir, fileName);
                var known = _state.Get(obj.Key);
                var localExists = File.Exists(fullPath);
                var localDirty = IsLocalDirty(fullPath, known);

                var action = SyncReconciler.DecideRemote(
                    obj.ETag, remoteExists: true, known, localExists, localDirty);
                var info = new PlaceholderInfo(fileName, obj.Key, obj.Size, obj.LastModifiedUtc);

                switch (action)
                {
                    case RemoteAction.CreatePlaceholder:
                        TryCreate(relativeDir, info, obj.Key, obj);
                        break;

                    case RemoteAction.UpdatePlaceholder:
                        // Only safe to refresh when there is no local data to lose.
                        if (CloudFilesProvider.IsDehydrated(fullPath))
                        {
                            try { File.Delete(fullPath); } catch { /* ignore */ }
                            TryCreate(relativeDir, info, obj.Key, obj);
                        }
                        else
                        {
                            _log?.Invoke($"Conflict (remote changed, local present): {obj.Key} — keeping local.");
                        }
                        break;

                    case RemoteAction.Conflict:
                        _log?.Invoke($"Conflict on {obj.Key} — keeping local copy.");
                        break;
                }
            }
        }

        // Objects we tracked in THIS folder that are gone remotely: remove clean cloud-only
        // placeholders. Entries belonging to sub-folders are that folder's business, not ours.
        var stale = new List<string>();
        foreach (var known in _state.EntriesWithPrefix(prefix))
        {
            if (known.Key.Length <= prefix.Length) continue;
            if (known.Key.IndexOf('/', prefix.Length) >= 0) continue; // lives in a sub-folder
            if (seen.Contains(known.Key)) continue;

            var fullPath = LocalPathForRelative(relativeDir, LeafName(known.Key));
            if (!File.Exists(fullPath)) { stale.Add(known.Key); continue; }
            if (CloudFilesProvider.IsDehydrated(fullPath))
            {
                try { File.Delete(fullPath); stale.Add(known.Key); }
                catch (Exception ex) { _log?.Invoke($"Remove '{known.Key}' failed: {ex.Message}"); }
            }
            else
            {
                _log?.Invoke($"Conflict (deleted remotely, local present): {known.Key} — keeping local.");
            }
        }

        if (stale.Count > 0)
        {
            _state.RemoveMany(stale);
            _log?.Invoke($"Removed {stale.Count} entr(ies) deleted remotely from '{Describe(prefix)}'.");
        }
    }

    /// <summary>Creates one placeholder, recording its remote state when it is a file.</summary>
    private void TryCreate(string relativeDir, PlaceholderInfo info, string key, S3ObjectEntry? obj = null)
    {
        try
        {
            _provider.CreatePlaceholders(relativeDir, new[] { info });
            if (obj is not null) RecordRemoteState(obj);
        }
        catch (Exception ex) { _log?.Invoke($"Creating placeholder '{key}' failed: {ex.Message}"); }
    }

    private bool IsLocalDirty(string fullPath, SyncEntry? known)
    {
        try
        {
            if (!File.Exists(fullPath)) return false;
            if (CloudFilesProvider.IsDehydrated(fullPath)) return false; // no local data
            var info = new FileInfo(fullPath);
            return known is null
                || known.Size != info.Length
                || known.LocalModifiedUtcTicks != info.LastWriteTimeUtc.Ticks;
        }
        catch { return false; }
    }

    private void RecordRemoteState(S3ObjectEntry obj) => _state.Set(new SyncEntry
    {
        Key = obj.Key,
        ETag = obj.ETag,
        Size = obj.Size,
        RemoteModifiedUtcTicks = obj.LastModifiedUtc.Ticks,
    });

    /// <summary>The part of a key or prefix after the last "/".</summary>
    private static string LeafName(string key)
    {
        var slash = key.LastIndexOf('/');
        return slash < 0 ? key : key[(slash + 1)..];
    }

    /// <summary>
    /// The sync-root-relative directory for a bucket prefix, or null when the prefix does not sit
    /// under this mapping's sub-path (a stale row from an earlier configuration).
    /// </summary>
    private string? RelativeDirFor(string prefix)
    {
        if (!prefix.StartsWith(_prefix, StringComparison.Ordinal)) return null;
        var sub = prefix[_prefix.Length..].Trim('/');
        return sub.Length == 0 ? string.Empty : sub.Replace('/', Path.DirectorySeparatorChar);
    }

    private string LocalPathForRelative(string relativeDir, string fileName) =>
        Path.Combine(SyncRootPath, relativeDir.Length == 0 ? fileName : Path.Combine(relativeDir, fileName));

    private static string Describe(string prefix) => prefix.Length == 0 ? "/" : prefix;

    /// <summary>
    /// "Return to cloud after some time": dehydrates hydrated, unpinned files whose last access is
    /// older than <paramref name="idleFor"/>. Windows Storage Sense can also do this automatically
    /// (we enable that policy), but this gives us an explicit fallback.
    ///
    /// It walks the listed folders one level at a time on purpose. A recursive sweep would
    /// enumerate directory placeholders that have never been opened, and enumerating one is exactly
    /// what makes Windows ask us to fill it in — a background cleanup would end up populating the
    /// whole bucket.
    /// </summary>
    public int DehydrateIdle(TimeSpan idleFor)
    {
        if (!Directory.Exists(SyncRootPath)) return 0;
        var cutoff = DateTime.Now - idleFor;
        var dehydrated = 0;

        foreach (var prefix in _state.ListedDirectories())
        {
            var relativeDir = RelativeDirFor(prefix);
            if (relativeDir is null) continue;
            var dir = relativeDir.Length == 0 ? SyncRootPath : Path.Combine(SyncRootPath, relativeDir);
            if (!Directory.Exists(dir)) continue;

            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(dir, "*", SearchOption.TopDirectoryOnly); }
            catch (Exception ex) { _log?.Invoke($"Scanning '{dir}' failed: {ex.Message}"); continue; }

            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    // A cloud-only placeholder carries RECALL_ON_DATA_ACCESS — already dehydrated.
                    if (((int)info.Attributes & FileAttributeRecallOnDataAccess) != 0) continue;
                    // Pinned files carry the PINNED attribute; leave them on disk.
                    if (((int)info.Attributes & FileAttributePinned) != 0) continue;
                    if (info.LastAccessTime > cutoff) continue;
                    _provider.Dehydrate(file);
                    dehydrated++;
                }
                catch (Exception ex) { _log?.Invoke($"Dehydrate '{file}' failed: {ex.Message}"); }
            }
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
        if (string.IsNullOrWhiteSpace(exe)) return;

        var iconResource = exe + ",0";
        FolderBranding.Apply(SyncRootPath, iconResource,
            tooltip: $"WasabiDrive — {_mapping.BucketName} (Files On-Demand)");

        var displayName = string.IsNullOrWhiteSpace(_mapping.Name) ? _mapping.BucketName : _mapping.Name;
        NavPaneRegistration.Register(_mapping.Id, $"WasabiDrive - {displayName}", SyncRootPath, iconResource);
    }

    /// <summary>Removes the Explorer sidebar entry for a mapping (used when it is deleted).</summary>
    public static void RemoveNavPaneEntry(Guid mappingId) => NavPaneRegistration.Unregister(mappingId);

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
        _remotePull?.Dispose();
        _syncer?.Dispose();
        _provider.Dispose();
        _s3.Dispose();
        _state.Dispose();
    }
}
