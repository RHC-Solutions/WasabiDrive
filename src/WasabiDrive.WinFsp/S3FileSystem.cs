using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Fsp;
using WasabiDrive.CloudFiles;
using WasabiDrive.Core.Models;
using FileInfo = Fsp.Interop.FileInfo;
using VolumeInfo = Fsp.Interop.VolumeInfo;

namespace WasabiDrive.WinFsp;

/// <summary>
/// A WinFsp filesystem backed directly by <see cref="WasabiS3Client"/> — the drive-letter engine
/// with rclone.exe taken out of the picture. WinFsp still supplies the kernel-mode filesystem;
/// what moves in-process is the VFS: path lookup (<see cref="MetadataCache"/>) and file data
/// caching (<see cref="CachedFile"/>).
/// </summary>
internal sealed class S3FileSystem : FileSystemBase, IDisposable
{
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;
    private const uint FILE_ATTRIBUTE_NORMAL = 0x00000080;

    /// <summary>S3 has no capacity to report; claim 1 PiB so Explorer never shows a full disk.</summary>
    private const ulong FakeVolumeSize = 1024UL * 1024 * 1024 * 1024 * 1024;

    /// <summary>Shared state for one path with at least one handle open.</summary>
    private sealed class OpenNode
    {
        public required string Path { get; init; }
        public required string Key { get; init; }
        public required bool IsDirectory { get; init; }
        public CachedFile? File { get; set; }
        public int Refs;
        public bool PendingDelete;
        public DateTime LastWriteUtc = DateTime.UtcNow;
    }

    private readonly Mapping _mapping;
    private readonly WasabiS3Client _s3;
    private readonly S3Paths _paths;
    private readonly MetadataCache _meta;
    private readonly CacheSettings _cache;
    private readonly string _cacheDir;
    private readonly Action<string>? _log;
    private readonly byte[] _security;

    private readonly ConcurrentDictionary<string, OpenNode> _open =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly object _openGate = new();

    private FileSystemHost? _host;

    public S3FileSystem(Mapping mapping, WasabiCredentials credentials, Action<string>? log = null)
    {
        _mapping = mapping;
        _log = log;
        _cache = mapping.Cache;
        _s3 = WasabiS3Client.ForMapping(mapping, credentials);
        _paths = new S3Paths(mapping.SubPath);
        _meta = new MetadataCache(_s3, _paths, _cache.DirCacheTime, log);

        var root = string.IsNullOrWhiteSpace(_cache.CacheDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WasabiDrive", "winfsp-cache")
            : _cache.CacheDir!;
        _cacheDir = Path.Combine(root, mapping.Id.ToString("N"));

        // One permissive descriptor for the whole volume. S3 carries no ACLs, and the drive is
        // mounted in the user's own session, so per-object security would be theatre.
        var descriptor = new RawSecurityDescriptor(
            "O:BAG:BAD:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;WD)");
        _security = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(_security, 0);
    }

    // ---------------------------------------------------------------- lifecycle

    public override int Init(object host)
    {
        _host = (FileSystemHost)host;
        _host.SectorSize = 4096;
        _host.SectorsPerAllocationUnit = 1;
        _host.MaxComponentLength = 255;
        _host.FileInfoTimeout = (uint)Math.Clamp(_cache.DirCacheTime.TotalMilliseconds, 1000, uint.MaxValue);
        _host.CaseSensitiveSearch = false;
        _host.CasePreservedNames = true;
        _host.UnicodeOnDisk = true;
        _host.PersistentAcls = false;
        _host.ReparsePoints = false;
        _host.NamedStreams = false;
        _host.ExtendedAttributes = false;
        _host.VolumeCreationTime = 0;
        _host.VolumeSerialNumber = (uint)_mapping.Id.GetHashCode();
        _host.FileSystemName = "WasabiDrive";
        return STATUS_SUCCESS;
    }

    public override int GetVolumeInfo(out VolumeInfo volumeInfo)
    {
        volumeInfo = default;
        volumeInfo.TotalSize = FakeVolumeSize;
        volumeInfo.FreeSize = FakeVolumeSize;
        volumeInfo.SetVolumeLabel(_mapping.Name);
        return STATUS_SUCCESS;
    }

    // ---------------------------------------------------------------- lookup

    public override int GetSecurityByName(
        string fileName, out uint fileAttributes, ref byte[] securityDescriptor)
    {
        fileAttributes = 0;

        S3Node? node;
        try { node = _meta.Lookup(fileName); }
        catch (Exception ex) { return Fail(ex, $"lookup '{fileName}'"); }

        if (node is null) return STATUS_OBJECT_NAME_NOT_FOUND;

        fileAttributes = node.IsDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL;
        if (securityDescriptor is not null) securityDescriptor = _security;
        return STATUS_SUCCESS;
    }

    public override int Open(
        string fileName, uint createOptions, uint grantedAccess,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        S3Node? node;
        try { node = _meta.Lookup(fileName); }
        catch (Exception ex) { return Fail(ex, $"open '{fileName}'"); }

        if (node is null) return STATUS_OBJECT_NAME_NOT_FOUND;

        var isDirectoryRequest = (createOptions & FILE_DIRECTORY_FILE) != 0;
        if (isDirectoryRequest && !node.IsDirectory) return STATUS_NOT_A_DIRECTORY;
        if ((createOptions & FILE_NON_DIRECTORY_FILE) != 0 && node.IsDirectory)
            return STATUS_FILE_IS_A_DIRECTORY;

        try
        {
            var open = Acquire(fileName, node.IsDirectory, node.Size, createEmpty: false);
            fileNode = open;
            normalizedName = S3Paths.Normalize(fileName);
            fileInfo = ToFileInfo(open, node);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"open '{fileName}'"); }
    }

    public override int Create(
        string fileName, uint createOptions, uint grantedAccess, uint fileAttributes,
        byte[] securityDescriptor, ulong allocationSize,
        out object? fileNode, out object? fileDesc, out FileInfo fileInfo, out string? normalizedName)
    {
        fileNode = null;
        fileDesc = null;
        fileInfo = default;
        normalizedName = null;

        try
        {
            if (_meta.Exists(fileName)) return STATUS_OBJECT_NAME_COLLISION;

            var isDirectory = (createOptions & FILE_DIRECTORY_FILE) != 0;
            if (isDirectory)
            {
                // A 0-byte "folder/" marker, matching the rclone engine's DIRECTORY_MARKERS=true,
                // so a folder created here is visible to the other mode and to the Wasabi console.
                PutEmptyObject(_paths.ToDirPrefix(fileName));
                _meta.Upsert(fileName, new S3Node(S3Paths.Leaf(fileName), true, 0, DateTime.UtcNow, null));
                _meta.Invalidate(fileName);
            }
            else
            {
                _meta.Upsert(fileName, new S3Node(S3Paths.Leaf(fileName), false, 0, DateTime.UtcNow, null));
            }

            var open = Acquire(fileName, isDirectory, 0, createEmpty: !isDirectory);
            fileNode = open;
            normalizedName = S3Paths.Normalize(fileName);
            fileInfo = ToFileInfo(open, null);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"create '{fileName}'"); }
    }

    public override int Overwrite(
        object fileNode, object fileDesc, uint fileAttributes, bool replaceFileAttributes,
        ulong allocationSize, out FileInfo fileInfo)
    {
        fileInfo = default;
        var open = (OpenNode)fileNode;
        if (open.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;

        try
        {
            open.File!.Truncate();
            open.LastWriteUtc = DateTime.UtcNow;
            fileInfo = ToFileInfo(open, null);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"overwrite '{open.Path}'"); }
    }

    // ---------------------------------------------------------------- data

    public override int Read(
        object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        out uint bytesTransferred)
    {
        bytesTransferred = 0;
        var open = (OpenNode)fileNode;
        if (open.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;

        var file = open.File!;
        if ((long)offset >= file.Length) return STATUS_END_OF_FILE;

        try
        {
            var managed = new byte[length];
            var read = file.Read((long)offset, managed, (int)length);
            if (read <= 0) return STATUS_END_OF_FILE;

            Marshal.Copy(managed, 0, buffer, read);
            bytesTransferred = (uint)read;
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"read '{open.Path}'"); }
    }

    public override int Write(
        object fileNode, object fileDesc, IntPtr buffer, ulong offset, uint length,
        bool writeToEndOfFile, bool constrainedIo,
        out uint bytesTransferred, out FileInfo fileInfo)
    {
        bytesTransferred = 0;
        fileInfo = default;
        var open = (OpenNode)fileNode;
        if (open.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;

        var file = open.File!;
        try
        {
            var target = writeToEndOfFile ? file.Length : (long)offset;

            if (constrainedIo)
            {
                // Constrained IO must not extend the file; clip the request to what fits.
                if (target >= file.Length)
                {
                    fileInfo = ToFileInfo(open, null);
                    return STATUS_SUCCESS;
                }
                length = (uint)Math.Min(length, file.Length - target);
            }

            var managed = new byte[length];
            Marshal.Copy(buffer, managed, 0, (int)length);

            var written = file.Write(target, managed, (int)length);
            open.LastWriteUtc = DateTime.UtcNow;

            bytesTransferred = (uint)written;
            fileInfo = ToFileInfo(open, null);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"write '{open.Path}'"); }
    }

    public override int Flush(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        fileInfo = default;

        // A null node is a whole-volume flush; there is nothing buffered at that level.
        if (fileNode is not OpenNode open) return STATUS_SUCCESS;
        if (open.IsDirectory) return STATUS_SUCCESS;

        try
        {
            var etag = open.File!.Flush();
            if (etag is not null)
            {
                _meta.Upsert(open.Path, new S3Node(
                    S3Paths.Leaf(open.Path), false, open.File.Length, DateTime.UtcNow, etag));
            }
            fileInfo = ToFileInfo(open, null);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"flush '{open.Path}'"); }
    }

    public override int GetFileInfo(object fileNode, object fileDesc, out FileInfo fileInfo)
    {
        var open = (OpenNode)fileNode;
        fileInfo = ToFileInfo(open, null);
        return STATUS_SUCCESS;
    }

    public override int SetBasicInfo(
        object fileNode, object fileDesc, uint fileAttributes,
        ulong creationTime, ulong lastAccessTime, ulong lastWriteTime, ulong changeTime,
        out FileInfo fileInfo)
    {
        // Deliberately a no-op. An S3 object's timestamps cannot change without rewriting the
        // object, and attributes have nowhere to live at all, so honouring these would cost a full
        // re-upload per touched file — or a sidecar metadata object per file, which then has to be
        // kept consistent and would be invisible to the Wasabi console and to the other engine.
        // Accepting and discarding keeps writes cheap; the timestamp Explorer shows is the object's
        // LastModified, which is what the rclone engine reports too (--use-server-modtime).
        var open = (OpenNode)fileNode;
        fileInfo = ToFileInfo(open, null);
        return STATUS_SUCCESS;
    }

    public override int SetFileSize(
        object fileNode, object fileDesc, ulong newSize, bool setAllocationSize,
        out FileInfo fileInfo)
    {
        fileInfo = default;
        var open = (OpenNode)fileNode;
        if (open.IsDirectory) return STATUS_FILE_IS_A_DIRECTORY;

        try
        {
            // An allocation-size hint costs nothing to honour lazily; only a real size change matters.
            if (!setAllocationSize || (long)newSize < open.File!.Length)
                open.File!.SetLength((long)newSize);

            fileInfo = ToFileInfo(open, null);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"resize '{open.Path}'"); }
    }

    // ---------------------------------------------------------------- namespace

    public override int CanDelete(object fileNode, object fileDesc, string fileName)
    {
        var open = (OpenNode)fileNode;
        if (!open.IsDirectory) return STATUS_SUCCESS;

        try
        {
            _meta.Invalidate(fileName);
            return _meta.GetChildren(fileName).Count == 0
                ? STATUS_SUCCESS
                : STATUS_DIRECTORY_NOT_EMPTY;
        }
        catch (Exception ex) { return Fail(ex, $"delete check '{fileName}'"); }
    }

    public override int Rename(
        object fileNode, object fileDesc, string fileName, string newFileName, bool replaceIfExists)
    {
        var open = (OpenNode)fileNode;

        try
        {
            var existing = _meta.Lookup(newFileName);
            if (existing is not null)
            {
                if (!replaceIfExists || existing.IsDirectory) return STATUS_OBJECT_NAME_COLLISION;
                _s3.DeleteObjectAsync(_paths.ToKey(newFileName)).GetAwaiter().GetResult();
            }

            if (open.IsDirectory)
                RenameDirectory(fileName, newFileName);
            else
                _s3.MoveObjectAsync(_paths.ToKey(fileName), _paths.ToKey(newFileName))
                    .GetAwaiter().GetResult();

            _meta.Remove(fileName);
            _meta.Invalidate(S3Paths.Parent(fileName));
            _meta.Invalidate(S3Paths.Parent(newFileName));
            _meta.Invalidate(newFileName);
            return STATUS_SUCCESS;
        }
        catch (Exception ex) { return Fail(ex, $"rename '{fileName}' to '{newFileName}'"); }
    }

    /// <summary>
    /// S3 cannot rename a prefix, so a folder rename is one server-side copy + delete per object
    /// underneath it. Cheap in bandwidth, linear in request count — the same reason
    /// <c>S3BulkOperations</c> exists for the Cloud Files engine.
    /// </summary>
    private void RenameDirectory(string fileName, string newFileName)
    {
        var sourcePrefix = _paths.ToDirPrefix(fileName);
        var targetPrefix = _paths.ToDirPrefix(newFileName);
        var moved = 0;

        var enumerator = _s3.ListObjectsAsync(sourcePrefix).GetAsyncEnumerator();
        try
        {
            while (enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult())
            {
                var entry = enumerator.Current;
                var suffix = entry.Key[sourcePrefix.Length..];
                _s3.MoveObjectAsync(entry.Key, targetPrefix + suffix).GetAwaiter().GetResult();
                moved++;
            }
        }
        finally
        {
            enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        PutEmptyObject(targetPrefix);
        try { _s3.DeleteObjectAsync(sourcePrefix).GetAwaiter().GetResult(); } catch { }

        _meta.InvalidateAll();
        _log?.Invoke($"Renamed folder '{fileName}' to '{newFileName}' ({moved} objects moved).");
    }

    public override bool ReadDirectoryEntry(
        object fileNode, object fileDesc, string pattern, string marker,
        ref object? context, out string? fileName, out FileInfo fileInfo)
    {
        fileName = null;
        fileInfo = default;

        var open = (OpenNode)fileNode;
        if (!open.IsDirectory) return false;

        if (context is not IEnumerator<KeyValuePair<string, S3Node?>> enumerator)
        {
            List<KeyValuePair<string, S3Node?>> entries;
            try { entries = BuildListing(open.Path, marker); }
            catch (Exception ex)
            {
                _log?.Invoke($"Listing '{open.Path}' failed: {ex.Message}");
                return false;
            }

            enumerator = entries.GetEnumerator();
            context = enumerator;
        }

        while (enumerator.MoveNext())
        {
            var (name, node) = (enumerator.Current.Key, enumerator.Current.Value);
            fileName = name;
            fileInfo = node is null
                ? DirectoryInfo(DateTime.UtcNow)
                : node.IsDirectory
                    ? DirectoryInfo(node.LastModifiedUtc)
                    : RegularFileInfo(node.Size, node.LastModifiedUtc);
            return true;
        }

        return false;
    }

    private List<KeyValuePair<string, S3Node?>> BuildListing(string path, string? marker)
    {
        var entries = new List<KeyValuePair<string, S3Node?>>();

        // WinFsp expects "." and ".." from any directory but the root.
        if (!S3Paths.IsRoot(path))
        {
            entries.Add(new(".", null));
            entries.Add(new("..", null));
        }

        foreach (var child in _meta.GetChildren(path).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase))
            entries.Add(new(child.Key, child.Value));

        if (!string.IsNullOrEmpty(marker))
        {
            var index = entries.FindIndex(e => string.Equals(e.Key, marker, StringComparison.OrdinalIgnoreCase));
            if (index >= 0) entries.RemoveRange(0, index + 1);
        }

        return entries;
    }

    // ---------------------------------------------------------------- close

    public override void Cleanup(object fileNode, object fileDesc, string fileName, uint flags)
    {
        var open = (OpenNode)fileNode;

        try
        {
            if ((flags & CleanupDelete) != 0)
            {
                open.PendingDelete = true;

                if (open.IsDirectory)
                    _s3.DeleteObjectAsync(_paths.ToDirPrefix(fileName)).GetAwaiter().GetResult();
                else
                    _s3.DeleteObjectAsync(open.Key).GetAwaiter().GetResult();

                _meta.Remove(fileName);
                return;
            }

            if (!open.IsDirectory && open.File is { Dirty: true } file)
            {
                var etag = file.Flush();
                _meta.Upsert(fileName, new S3Node(
                    S3Paths.Leaf(fileName), false, file.Length, DateTime.UtcNow, etag));
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Cleanup of '{fileName}' failed: {ex.Message}");
        }
    }

    public override void Close(object fileNode, object fileDesc) => Release((OpenNode)fileNode);

    // ---------------------------------------------------------------- helpers

    private OpenNode Acquire(string path, bool isDirectory, long size, bool createEmpty)
    {
        var normalized = S3Paths.Normalize(path);

        lock (_openGate)
        {
            if (_open.TryGetValue(normalized, out var existing))
            {
                existing.Refs++;
                return existing;
            }

            var open = new OpenNode
            {
                Path = normalized,
                Key = _paths.ToKey(normalized),
                IsDirectory = isDirectory,
                Refs = 1,
            };

            if (!isDirectory)
            {
                open.File = new CachedFile(
                    _s3, open.Key, size, _cacheDir,
                    _cache.ReadChunkSizeMb, _cache.ReadChunkStreams, createEmpty, _log);
            }

            _open[normalized] = open;
            return open;
        }
    }

    private void Release(OpenNode open)
    {
        lock (_openGate)
        {
            if (--open.Refs > 0) return;
            _open.TryRemove(open.Path, out _);
        }

        open.File?.Dispose();
    }

    private void PutEmptyObject(string key)
    {
        var temp = Path.Combine(Path.GetTempPath(), "wd-empty-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllBytes(temp, Array.Empty<byte>());
            _s3.PutObjectAsync(key, temp).GetAwaiter().GetResult();
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }

    private FileInfo ToFileInfo(OpenNode open, S3Node? node)
    {
        if (open.IsDirectory) return DirectoryInfo(node?.LastModifiedUtc ?? DateTime.UtcNow);

        var size = open.File?.Length ?? node?.Size ?? 0;
        var written = open.File is { Dirty: true } ? open.LastWriteUtc
            : node?.LastModifiedUtc ?? open.LastWriteUtc;
        return RegularFileInfo(size, written);
    }

    private static FileInfo DirectoryInfo(DateTime timestampUtc)
    {
        var stamp = ToFileTime(timestampUtc);
        return new FileInfo
        {
            FileAttributes = FILE_ATTRIBUTE_DIRECTORY,
            FileSize = 0,
            AllocationSize = 0,
            CreationTime = stamp,
            LastAccessTime = stamp,
            LastWriteTime = stamp,
            ChangeTime = stamp,
            HardLinks = 0,
            IndexNumber = 0,
        };
    }

    private static FileInfo RegularFileInfo(long size, DateTime timestampUtc)
    {
        var stamp = ToFileTime(timestampUtc);
        return new FileInfo
        {
            FileAttributes = FILE_ATTRIBUTE_NORMAL,
            FileSize = (ulong)Math.Max(0, size),
            AllocationSize = (ulong)((Math.Max(0, size) + 4095) / 4096 * 4096),
            CreationTime = stamp,
            LastAccessTime = stamp,
            LastWriteTime = stamp,
            ChangeTime = stamp,
            HardLinks = 0,
            IndexNumber = 0,
        };
    }

    private static ulong ToFileTime(DateTime utc)
    {
        if (utc < new DateTime(1601, 1, 2, 0, 0, 0, DateTimeKind.Utc)) return 0;
        return (ulong)utc.ToFileTimeUtc();
    }

    private int Fail(Exception ex, string what)
    {
        _log?.Invoke($"WinFsp {what} failed: {ex.GetType().Name}: {ex.Message}");
        return STATUS_IO_DEVICE_ERROR;
    }

    public void Dispose()
    {
        foreach (var open in _open.Values) open.File?.Dispose();
        _open.Clear();
        _s3.Dispose();
    }
}
