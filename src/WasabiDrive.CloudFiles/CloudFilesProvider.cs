using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;

namespace WasabiDrive.CloudFiles;

/// <summary>
/// A Windows Cloud Files (cfapi) sync-root provider for one bucket. It registers a local folder as
/// a sync root, connects the hydration callback, and streams object bytes from Wasabi on demand.
/// This is the engine behind the OneDrive-style "Files On-Demand" experience: placeholders occupy
/// no disk space until opened, and Explorer shows the native status column, overlays, and the
/// "Always keep on this device" / "Free up space" menu items.
///
/// Directories are populated on demand as well as hydrated on demand. A folder starts life as a
/// bare directory placeholder holding nothing but its own name; the first time anything enumerates
/// it, Windows raises FETCH_PLACEHOLDERS and we list exactly that one prefix. Folders the user
/// never opens are never listed, so memory and request count track what is actually looked at
/// rather than the size of the bucket.
/// </summary>
public sealed class CloudFilesProvider : IDisposable
{
    private const string ProviderName = "WasabiDrive";

    private readonly string _syncRootPath;
    private readonly Guid _providerId;
    private readonly byte[] _syncRootIdentity;
    private readonly Func<string, long, long, CancellationToken, Task<Stream>> _openRead;
    private readonly Action<string>? _log;

    // The delegates must be kept alive for the whole connection or the GC will collect the thunks
    // that Windows calls back into.
    private CF_CALLBACK? _fetchDataCallback;
    private CF_CALLBACK? _fetchPlaceholdersCallback;
    private CF_CALLBACK? _cancelFetchPlaceholdersCallback;
    private CF_CONNECTION_KEY _connectionKey;
    private bool _connected;

    /// <summary>
    /// In-flight directory enumerations, keyed by transfer key, so CANCEL_FETCH_PLACEHOLDERS can
    /// abandon a listing when the user closes the folder before it finishes.
    /// </summary>
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _directoryFetches = new();

    /// <summary>
    /// Supplies the immediate contents of one bucket "directory" — the files directly under the
    /// given prefix plus its sub-folders. Set by the owner before <see cref="Connect"/>; when it is
    /// null, directories are left as-is (whatever was eagerly created stays, nothing is fetched).
    /// </summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<PlaceholderInfo>>>? DirectorySource { get; set; }

    /// <summary>
    /// The bucket prefix the sync root maps to ("" for the whole bucket, otherwise ending in "/").
    /// Used to rebuild a directory's prefix from its path when it carries no file identity of its
    /// own — the sync root itself, and any folder the user created locally.
    /// </summary>
    public string RootPrefix { get; set; } = string.Empty;

    /// <param name="syncRootPath">Local folder to register as the sync root.</param>
    /// <param name="providerId">Stable per-mapping provider GUID.</param>
    /// <param name="syncRootIdentity">Opaque bytes identifying this root (e.g. the remote name).</param>
    /// <param name="openRead">Fetches object bytes: (s3key, offset, length, ct) -> stream.</param>
    public CloudFilesProvider(
        string syncRootPath,
        Guid providerId,
        byte[] syncRootIdentity,
        Func<string, long, long, CancellationToken, Task<Stream>> openRead,
        Action<string>? log = null)
    {
        _syncRootPath = syncRootPath ?? throw new ArgumentNullException(nameof(syncRootPath));
        _providerId = providerId;
        _syncRootIdentity = syncRootIdentity;
        _openRead = openRead ?? throw new ArgumentNullException(nameof(openRead));
        _log = log;
    }

    public string SyncRootPath => _syncRootPath;

    /// <summary>Raised (with the S3 key) after a file finishes hydrating, so watchers can ignore
    /// the resulting disk writes rather than treating them as user edits.</summary>
    public event Action<string>? Hydrated;

    /// <summary>True if a sync root is already registered at this path.</summary>
    public unsafe bool IsRegistered()
    {
        // CfGetSyncRootInfoByPath returns S_OK for a registered root, an error otherwise.
        uint returned;
        var basicBuf = stackalloc byte[512];
        var hr = PInvoke.CfGetSyncRootInfoByPath(
            _syncRootPath, CF_SYNC_ROOT_INFO_CLASS.CF_SYNC_ROOT_INFO_BASIC,
            basicBuf, 512, &returned);
        return hr.Succeeded;
    }

    /// <summary>Registers the folder as a sync root (idempotent via CF_REGISTER_FLAG_UPDATE).</summary>
    public unsafe void Register()
    {
        Directory.CreateDirectory(_syncRootPath);

        var version = "1.0";
        // The root's own file identity is the prefix it maps to, so a FETCH_PLACEHOLDERS raised
        // against the root resolves the same way as one raised against any sub-folder.
        var rootIdentity = Encoding.UTF8.GetBytes(RootPrefix);
        fixed (char* pName = ProviderName)
        fixed (char* pVersion = version)
        fixed (byte* pIdentity = _syncRootIdentity)
        fixed (byte* pRootFileIdentity = rootIdentity)
        {
            var registration = new CF_SYNC_REGISTRATION
            {
                StructSize = (uint)sizeof(CF_SYNC_REGISTRATION),
                ProviderName = pName,
                ProviderVersion = pVersion,
                SyncRootIdentity = pIdentity,
                SyncRootIdentityLength = (uint)_syncRootIdentity.Length,
                FileIdentity = pRootFileIdentity,
                FileIdentityLength = (uint)rootIdentity.Length,
                ProviderId = _providerId,
            };

            var policies = new CF_SYNC_POLICIES
            {
                StructSize = (uint)sizeof(CF_SYNC_POLICIES),
                Hydration = new CF_HYDRATION_POLICY
                {
                    Primary = CF_HYDRATION_POLICY_PRIMARY.CF_HYDRATION_POLICY_FULL,
                    Modifier = CF_HYDRATION_POLICY_MODIFIER.CF_HYDRATION_POLICY_MODIFIER_AUTO_DEHYDRATION_ALLOWED,
                },
                // FULL means this provider owns the namespace and is responsible for filling it in
                // — not that it has to be filled in up front. Whether a given directory is
                // enumerated on demand is decided per placeholder: a directory placeholder created
                // without CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION stays empty until
                // something opens it, and Windows then asks us for its contents.
                // (CF_POPULATION_POLICY_PARTIAL, the obvious-looking choice, is documented as not
                // supported.)
                Population = new CF_POPULATION_POLICY
                {
                    Primary = CF_POPULATION_POLICY_PRIMARY.CF_POPULATION_POLICY_FULL,
                    Modifier = CF_POPULATION_POLICY_MODIFIER.CF_POPULATION_POLICY_MODIFIER_NONE,
                },
                InSync = CF_INSYNC_POLICY.CF_INSYNC_POLICY_NONE,
            };

            PInvoke.CfRegisterSyncRoot(_syncRootPath, registration, policies,
                CF_REGISTER_FLAGS.CF_REGISTER_FLAG_UPDATE).ThrowOnFailure();
        }
        _log?.Invoke($"Registered sync root at {_syncRootPath}");
    }

    /// <summary>Connects hydration callbacks. Call once after <see cref="Register"/>.</summary>
    public unsafe void Connect()
    {
        if (_connected) return;

        _fetchDataCallback = OnFetchData;
        _fetchPlaceholdersCallback = OnFetchPlaceholders;
        _cancelFetchPlaceholdersCallback = OnCancelFetchPlaceholders;
        var callbacks = new[]
        {
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = _fetchDataCallback,
            },
            // Raised when something enumerates a directory placeholder we left unpopulated.
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = _fetchPlaceholdersCallback,
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_PLACEHOLDERS,
                Callback = _cancelFetchPlaceholdersCallback,
            },
            new CF_CALLBACK_REGISTRATION
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE, // terminator
            },
        };

        CF_CONNECTION_KEY key;
        PInvoke.CfConnectSyncRoot(
            _syncRootPath, callbacks, null,
            CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_PROCESS_INFO
            | CF_CONNECT_FLAGS.CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH,
            &key).ThrowOnFailure();

        _connectionKey = key;
        _connected = true;
        _log?.Invoke("Connected sync root (hydration active).");
    }

    public void Disconnect()
    {
        if (!_connected) return;

        // Abandon any directory listing still in flight before the connection goes away; its
        // CfExecute would fail against a dead connection key anyway. Each listing removes and
        // disposes its own token source as it unwinds, so cancelling is all that is needed here.
        foreach (var cts in _directoryFetches.Values)
        {
            try { cts.Cancel(); } catch { /* already completing */ }
        }

        PInvoke.CfDisconnectSyncRoot(_connectionKey);
        _connected = false;
        _fetchDataCallback = null;
        _fetchPlaceholdersCallback = null;
        _cancelFetchPlaceholdersCallback = null;
    }

    /// <summary>Removes the sync-root registration (does not delete local files).</summary>
    public void Unregister()
    {
        Disconnect();
        try { PInvoke.CfUnregisterSyncRoot(_syncRootPath); }
        catch (Exception ex) { _log?.Invoke($"Unregister failed: {ex.Message}"); }
    }

    /// <summary>
    /// Creates placeholders in <paramref name="relativeDir"/> (relative to the sync root). Entries
    /// marked <see cref="PlaceholderInfo.IsDirectory"/> become directory placeholders that stay
    /// empty until something enumerates them; the rest become cloud-only file placeholders.
    /// </summary>
    public unsafe int CreatePlaceholders(string relativeDir, IReadOnlyList<PlaceholderInfo> files)
    {
        if (files.Count == 0) return 0;

        var baseDir = string.IsNullOrEmpty(relativeDir)
            ? _syncRootPath
            : Path.Combine(_syncRootPath, relativeDir);
        Directory.CreateDirectory(baseDir);

        var allocations = new List<IntPtr>(files.Count * 2);
        try
        {
            var infos = BuildCreateInfos(files, allocations);
            uint processed;
            PInvoke.CfCreatePlaceholders(baseDir, infos, CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE, &processed)
                .ThrowOnFailure();
            return (int)processed;
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// Marshals placeholder entries into the native array cfapi wants. The unmanaged name and
    /// identity buffers are recorded in <paramref name="allocations"/> for the caller to free once
    /// the native call has returned — they must stay alive for its whole duration.
    /// </summary>
    private static unsafe CF_PLACEHOLDER_CREATE_INFO[] BuildCreateInfos(
        IReadOnlyList<PlaceholderInfo> files, List<IntPtr> allocations)
    {
        var infos = new CF_PLACEHOLDER_CREATE_INFO[files.Count];
        for (var i = 0; i < files.Count; i++)
        {
            var f = files[i];
            var namePtr = Marshal.StringToHGlobalUni(f.FileName);
            allocations.Add(namePtr);

            var identityBytes = Encoding.UTF8.GetBytes(f.FileIdentity);
            var idPtr = Marshal.AllocHGlobal(Math.Max(1, identityBytes.Length));
            Marshal.Copy(identityBytes, 0, idPtr, identityBytes.Length);
            allocations.Add(idPtr);

            var stamp = f.LastModifiedUtc.ToFileTimeUtc();
            infos[i] = new CF_PLACEHOLDER_CREATE_INFO
            {
                RelativeFileName = (char*)namePtr,
                FsMetadata = new CF_FS_METADATA
                {
                    FileSize = f.IsDirectory ? 0 : f.Size,
                    BasicInfo = new FILE_BASIC_INFO
                    {
                        CreationTime = stamp,
                        LastWriteTime = stamp,
                        ChangeTime = stamp,
                        LastAccessTime = stamp,
                        FileAttributes = (uint)(f.IsDirectory
                            ? FileAttributes.Directory
                            : FileAttributes.Normal),
                    },
                },
                FileIdentity = (void*)idPtr,
                FileIdentityLength = (uint)identityBytes.Length,
                // Deliberately no CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION: that is
                // exactly the flag that would force a directory to be filled in up front. Leaving
                // it off is what makes a folder wait to be opened before it is listed.
                Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC,
            };
        }
        return infos;
    }

    /// <summary>Pins ("Always keep on this device") or unpins a hydrated/placeholder file.</summary>
    public unsafe void SetPinned(string fullPath, bool pinned)
    {
        using var handle = OpenFileHandle(fullPath);
        PInvoke.CfSetPinState(handle,
            pinned ? CF_PIN_STATE.CF_PIN_STATE_PINNED : CF_PIN_STATE.CF_PIN_STATE_UNPINNED,
            CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE, null).ThrowOnFailure();
    }

    /// <summary>"Free up space": releases the local bytes, leaving a cloud-only placeholder.</summary>
    public unsafe void Dehydrate(string fullPath)
    {
        using var handle = OpenFileHandle(fullPath);
        var length = new FileInfo(fullPath).Length;
        PInvoke.CfDehydratePlaceholder(handle, 0, length,
            CF_DEHYDRATE_FLAGS.CF_DEHYDRATE_FLAG_NONE, null).ThrowOnFailure();
    }

    /// <summary>Marks a file as in-sync (used after a local change is uploaded to the cloud).</summary>
    public unsafe void MarkInSync(string fullPath)
    {
        using var handle = OpenFileHandle(fullPath);
        PInvoke.CfSetInSyncState(handle, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE, null).ThrowOnFailure();
    }

    /// <summary>
    /// Converts a normal file created locally into an in-sync placeholder participating in
    /// on-demand (called after the new file has been uploaded).
    /// </summary>
    public unsafe void ConvertToPlaceholder(string fullPath, string fileIdentity)
    {
        var identity = Encoding.UTF8.GetBytes(fileIdentity);
        using var handle = OpenFileHandle(fullPath);
        fixed (byte* pId = identity)
        {
            PInvoke.CfConvertToPlaceholder(handle, pId, (uint)identity.Length,
                CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC, null, null).ThrowOnFailure();
        }
    }

    /// <summary>True if the file is a cloud-only (dehydrated) placeholder with no local data.</summary>
    public static bool IsDehydrated(string fullPath) =>
        ((int)new FileInfo(fullPath).Attributes & FileAttributeRecallOnDataAccess) != 0;

    private const int FileAttributeRecallOnDataAccess = 0x00400000;

    private static SafeFileHandle OpenFileHandle(string path) =>
        File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

    /// <summary>Cloud Files FETCH_DATA callback: Windows needs a byte range hydrated.</summary>
    private unsafe void OnFetchData(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var fetch = callbackParameters->Anonymous.FetchData;
        var connectionKey = callbackInfo->ConnectionKey;
        var transferKey = callbackInfo->TransferKey;
        var offset = fetch.RequiredFileOffset;
        var length = fetch.RequiredLength;

        var key = Encoding.UTF8.GetString(
            (byte*)callbackInfo->FileIdentity, (int)callbackInfo->FileIdentityLength);

        // Service the transfer off the callback thread (S3 I/O must not block the filter callback).
        _ = Task.Run(() => HydrateAsync(connectionKey, transferKey, key, offset, length));
    }

    /// <summary>
    /// Cloud Files FETCH_PLACEHOLDERS callback: something is enumerating a directory placeholder we
    /// left unpopulated, so Windows is asking what is inside it. This is the whole point of the
    /// lazy design — it fires once per folder the user actually opens.
    /// </summary>
    private unsafe void OnFetchPlaceholders(
        CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var connectionKey = callbackInfo->ConnectionKey;
        long transferKey = callbackInfo->TransferKey;
        var prefix = DirectoryPrefixFor(callbackInfo);

        var source = DirectorySource;
        if (source is null)
        {
            // Report an empty but complete directory rather than leaving Explorer blocked on a
            // transfer nobody is going to answer.
            TransferPlaceholders(connectionKey, transferKey, Array.Empty<PlaceholderInfo>(), NtStatusSuccess);
            return;
        }

        var cts = new CancellationTokenSource();
        _directoryFetches[transferKey] = cts;
        _ = Task.Run(() => PopulateDirectoryAsync(connectionKey, transferKey, prefix, source, cts));
    }

    /// <summary>
    /// Cloud Files CANCEL_FETCH_PLACEHOLDERS callback: the enumeration was abandoned (the folder
    /// was closed, or the request timed out). Stop listing rather than finishing a listing whose
    /// result nobody will read.
    /// </summary>
    private unsafe void OnCancelFetchPlaceholders(
        CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        long transferKey = callbackInfo->TransferKey;
        if (_directoryFetches.TryGetValue(transferKey, out var cts))
        {
            try { cts.Cancel(); } catch { /* already completing */ }
        }
    }

    private async Task PopulateDirectoryAsync(
        CF_CONNECTION_KEY connectionKey,
        long transferKey,
        string prefix,
        Func<string, CancellationToken, Task<IReadOnlyList<PlaceholderInfo>>> source,
        CancellationTokenSource cts)
    {
        try
        {
            var entries = await source(prefix, cts.Token).ConfigureAwait(false);
            TransferPlaceholders(connectionKey, transferKey, entries, NtStatusSuccess);
        }
        catch (OperationCanceledException)
        {
            TransferPlaceholders(connectionKey, transferKey, Array.Empty<PlaceholderInfo>(), NtStatusCancelled);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Listing '{(prefix.Length == 0 ? "/" : prefix)}' failed: {ex.Message}");
            TransferPlaceholders(connectionKey, transferKey, Array.Empty<PlaceholderInfo>(), NtStatusIoDeviceError);
        }
        finally
        {
            if (_directoryFetches.TryRemove(transferKey, out var finished)) finished.Dispose();
        }
    }

    /// <summary>
    /// Answers an in-flight FETCH_PLACEHOLDERS with a directory's contents. Failures are logged
    /// rather than thrown: this runs on a detached task, and an escaping exception would take the
    /// process down while leaving Explorer waiting anyway.
    /// </summary>
    private unsafe void TransferPlaceholders(
        CF_CONNECTION_KEY connectionKey, long transferKey,
        IReadOnlyList<PlaceholderInfo> entries, int completionStatus)
    {
        var allocations = new List<IntPtr>(entries.Count * 2);
        try
        {
            var infos = entries.Count == 0
                ? Array.Empty<CF_PLACEHOLDER_CREATE_INFO>()
                : BuildCreateInfos(entries, allocations);

            fixed (CF_PLACEHOLDER_CREATE_INFO* pInfos = infos)
            {
                var opInfo = new CF_OPERATION_INFO
                {
                    StructSize = (uint)sizeof(CF_OPERATION_INFO),
                    Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                    ConnectionKey = connectionKey,
                    TransferKey = transferKey,
                };
                var opParams = new CF_OPERATION_PARAMETERS { ParamSize = TransferPlaceholdersParamSize };
                // DISABLE_ON_DEMAND_POPULATION here means "this directory is now complete": Windows
                // records the contents on disk and serves later enumerations itself, so re-opening
                // the folder costs no request at all until the reconcile refreshes it.
                opParams.Anonymous.TransferPlaceholders.Flags =
                    CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_DISABLE_ON_DEMAND_POPULATION;
                opParams.Anonymous.TransferPlaceholders.CompletionStatus = new NTSTATUS(completionStatus);
                opParams.Anonymous.TransferPlaceholders.PlaceholderTotalCount = entries.Count;
                opParams.Anonymous.TransferPlaceholders.PlaceholderArray = pInfos;
                opParams.Anonymous.TransferPlaceholders.PlaceholderCount = (uint)infos.Length;
                opParams.Anonymous.TransferPlaceholders.EntriesProcessed = 0;

                PInvoke.CfExecute(opInfo, ref opParams).ThrowOnFailure();

                var processed = opParams.Anonymous.TransferPlaceholders.EntriesProcessed;
                if (infos.Length > 0 && processed < infos.Length)
                    _log?.Invoke(
                        $"Directory listing partially accepted: {processed} of {infos.Length} entries.");
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Transferring a directory listing failed: {ex.Message}");
        }
        finally
        {
            foreach (var p in allocations) Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// The bucket prefix a callback refers to. The file identity we stamped onto the directory
    /// placeholder is the prefix itself, which makes this exact; the path fallback covers the two
    /// cases that have no identity of ours — the sync root, and folders the user created locally.
    /// </summary>
    private unsafe string DirectoryPrefixFor(CF_CALLBACK_INFO* info)
    {
        if (info->FileIdentity != null && info->FileIdentityLength > 0)
            return Encoding.UTF8.GetString((byte*)info->FileIdentity, (int)info->FileIdentityLength);

        var normalized = info->NormalizedPath.ToString() ?? string.Empty;
        // CF_CONNECT_FLAG_REQUIRE_FULL_FILE_PATH gives a volume-absolute path, which may or may not
        // already carry the drive letter; VolumeDosName supplies it when it doesn't.
        var full = normalized.Length > 1 && normalized[1] == ':'
            ? normalized
            : (info->VolumeDosName.ToString() ?? string.Empty) + normalized;

        string relative;
        try { relative = Path.GetRelativePath(_syncRootPath, full); }
        catch { return RootPrefix; }

        if (relative == "." || relative.StartsWith("..", StringComparison.Ordinal))
            return RootPrefix;

        var sub = relative.Replace(Path.DirectorySeparatorChar, '/').Trim('/');
        return sub.Length == 0 ? RootPrefix : RootPrefix + sub + "/";
    }

    private const int NtStatusSuccess = 0;
    private const int NtStatusCancelled = unchecked((int)0xC0000120);    // STATUS_CANCELLED
    private const int NtStatusIoDeviceError = unchecked((int)0xC0000185); // STATUS_IO_DEVICE_ERROR

    /// <summary>Size of each parallel hydration range request. A multiple of the sector size.</summary>
    private const int HydrationChunkSizeBytes = 4 << 20; // 4 MiB

    /// <summary>Concurrent range GETs per hydration. Peak buffer use is this × the chunk size.</summary>
    private const int HydrationStreams = 8;

    private async Task HydrateAsync(CF_CONNECTION_KEY connectionKey, long transferKey, string key, long offset, long length)
    {
        try
        {
            // One sequential GET leaves most of the link idle: Wasabi serves a single stream far
            // slower than several in parallel. Anything spanning more than one chunk is fetched as
            // concurrent ranges; small reads (thumbnails, metadata probes) keep the simple path,
            // where the extra requests would cost more than they save.
            if (length > HydrationChunkSizeBytes)
                await HydrateParallelAsync(connectionKey, transferKey, key, offset, length).ConfigureAwait(false);
            else
                await HydrateSequentialAsync(connectionKey, transferKey, key, offset, length).ConfigureAwait(false);

            Hydrated?.Invoke(key);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Hydration of '{key}' failed: {ex.Message}");
            TransferError(connectionKey, transferKey, offset, length);
        }
    }

    private async Task HydrateSequentialAsync(
        CF_CONNECTION_KEY connectionKey, long transferKey, string key, long offset, long length)
    {
        await using var stream = await _openRead(key, offset, length, CancellationToken.None).ConfigureAwait(false);

        // cfapi requires each non-final chunk length to be a multiple of the sector size.
        const int chunkSize = 1 << 20; // 1 MiB
        var buffer = new byte[chunkSize];
        var position = offset;
        var remaining = length;

        while (remaining > 0)
        {
            var want = (int)Math.Min(chunkSize, remaining);
            var read = await ReadAtLeastAsync(stream, buffer, want).ConfigureAwait(false);
            if (read <= 0) break;
            TransferData(connectionKey, transferKey, buffer, position, read);
            position += read;
            remaining -= read;
        }
    }

    private async Task HydrateParallelAsync(
        CF_CONNECTION_KEY connectionKey, long transferKey, string key, long offset, long length)
    {
        var end = offset + length;
        var chunks = new List<(long Offset, int Length)>();
        for (var pos = offset; pos < end; pos += HydrationChunkSizeBytes)
            chunks.Add((pos, (int)Math.Min(HydrationChunkSizeBytes, end - pos)));

        // CfExecute calls for a single transfer key are serialised through this gate; only the
        // network reads overlap. Windows accepts the completed ranges in any order.
        using var transferGate = new SemaphoreSlim(1, 1);

        await Parallel.ForEachAsync(
            chunks,
            new ParallelOptions { MaxDegreeOfParallelism = HydrationStreams },
            async (chunk, ct) =>
            {
                var buffer = ArrayPool<byte>.Shared.Rent(chunk.Length);
                try
                {
                    await using var stream = await _openRead(key, chunk.Offset, chunk.Length, ct)
                        .ConfigureAwait(false);
                    var read = await ReadAtLeastAsync(stream, buffer, chunk.Length).ConfigureAwait(false);

                    // A short read would leave a hole in the range, and Windows would wait forever
                    // for bytes that are never coming. Fail loudly so the outer catch reports it.
                    if (read < chunk.Length)
                        throw new IOException(
                            $"Short read at offset {chunk.Offset}: wanted {chunk.Length} bytes, got {read}.");

                    await transferGate.WaitAsync(ct).ConfigureAwait(false);
                    try { TransferData(connectionKey, transferKey, buffer, chunk.Offset, read); }
                    finally { transferGate.Release(); }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }).ConfigureAwait(false);
    }

    private static async Task<int> ReadAtLeastAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total)).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private unsafe void TransferData(CF_CONNECTION_KEY connectionKey, long transferKey, byte[] buffer, long offset, int count)
    {
        fixed (byte* p = buffer)
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
            };
            var opParams = new CF_OPERATION_PARAMETERS { ParamSize = TransferDataParamSize };
            opParams.Anonymous.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            opParams.Anonymous.TransferData.CompletionStatus = new NTSTATUS(0); // STATUS_SUCCESS
            opParams.Anonymous.TransferData.Buffer = p;
            opParams.Anonymous.TransferData.Offset = offset;
            opParams.Anonymous.TransferData.Length = count;
            PInvoke.CfExecute(opInfo, ref opParams).ThrowOnFailure();
        }
    }

    private unsafe void TransferError(CF_CONNECTION_KEY connectionKey, long transferKey, long offset, long length)
    {
        try
        {
            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
                ConnectionKey = connectionKey,
                TransferKey = transferKey,
            };
            var opParams = new CF_OPERATION_PARAMETERS { ParamSize = TransferDataParamSize };
            opParams.Anonymous.TransferData.Flags = CF_OPERATION_TRANSFER_DATA_FLAGS.CF_OPERATION_TRANSFER_DATA_FLAG_NONE;
            opParams.Anonymous.TransferData.CompletionStatus = new NTSTATUS(unchecked((int)0xC0000185)); // STATUS_IO_DEVICE_ERROR
            opParams.Anonymous.TransferData.Buffer = null;
            opParams.Anonymous.TransferData.Offset = offset;
            opParams.Anonymous.TransferData.Length = length;
            PInvoke.CfExecute(opInfo, ref opParams);
        }
        catch { /* nothing more we can do */ }
    }

    // ParamSize for a TRANSFER_DATA op = offset of the union + size of the TransferData member.
    private static readonly uint TransferDataParamSize =
        (uint)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>(nameof(CF_OPERATION_PARAMETERS.Anonymous))
        + (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferData_e__Struct>();

    // Likewise for TRANSFER_PLACEHOLDERS, whose union member is a different size.
    private static readonly uint TransferPlaceholdersParamSize =
        (uint)Marshal.OffsetOf<CF_OPERATION_PARAMETERS>(nameof(CF_OPERATION_PARAMETERS.Anonymous))
        + (uint)Marshal.SizeOf<CF_OPERATION_PARAMETERS._Anonymous_e__Union._TransferPlaceholders_e__Struct>();

    public void Dispose() => Disconnect();
}

/// <summary>Data needed to create one placeholder.</summary>
/// <param name="FileName">Leaf name within its directory.</param>
/// <param name="FileIdentity">
/// Opaque identity handed back to us later: the full S3 key for a file, the directory's prefix
/// (ending in "/") for a directory.
/// </param>
/// <param name="Size">File size in bytes; ignored for directories.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp.</param>
/// <param name="IsDirectory">
/// True for a sub-folder. Directory placeholders are created empty and populated only when
/// something enumerates them.
/// </param>
public sealed record PlaceholderInfo(
    string FileName, string FileIdentity, long Size, DateTime LastModifiedUtc, bool IsDirectory = false);
