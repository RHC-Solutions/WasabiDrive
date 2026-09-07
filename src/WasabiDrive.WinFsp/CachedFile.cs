using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WasabiDrive.CloudFiles;

namespace WasabiDrive.WinFsp;

/// <summary>
/// The write-back cache for one open object — the piece rclone's VFS would otherwise provide.
///
/// Bytes live in a local backing file. Reads hydrate only the chunks the caller touches, using
/// parallel range GETs. Writes go into the backing file and record which byte ranges changed, and
/// on last-handle close the object is rebuilt with a multipart upload that uploads only the parts
/// containing changed bytes and takes every other part straight from the previous version of the
/// object with a server-side <c>UploadPartCopy</c>. Editing a few MB of a multi-GB file therefore
/// costs a few MB of upload rather than the whole object.
///
/// A whole-object PUT is still used where splicing cannot pay off: a newly created or fully
/// overwritten object, an object no larger than one part, or one whose changes cover almost all
/// of it.
/// </summary>
internal sealed class CachedFile : IDisposable
{
    private readonly WasabiS3Client _s3;
    private readonly string _key;
    private readonly string _backingPath;
    private readonly int _chunkSize;
    private readonly int _maxStreams;
    private readonly Action<string>? _log;

    private readonly SafeFileHandle _handle;
    private readonly HashSet<long> _present = new();
    private readonly object _presentGate = new();
    private readonly DirtyRanges _dirtyRanges = new();
    private readonly object _dirtyGate = new();
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private long _length;
    private long _remoteLength;
    private bool _fullUploadRequired;
    private bool _lengthChanged;

    public long Length => Interlocked.Read(ref _length);
    public string Key => _key;

    public bool Dirty
    {
        get
        {
            if (_fullUploadRequired || _lengthChanged) return true;
            lock (_dirtyGate) return !_dirtyRanges.IsEmpty;
        }
    }

    public CachedFile(
        WasabiS3Client s3, string key, long remoteSize, string cacheDir,
        int chunkSizeMb, int maxStreams, bool createEmpty, Action<string>? log)
    {
        _s3 = s3;
        _key = key;
        _chunkSize = Math.Max(1, chunkSizeMb) * 1024 * 1024;
        _maxStreams = Math.Max(1, maxStreams);
        _log = log;
        _remoteLength = remoteSize;
        _length = remoteSize;

        Directory.CreateDirectory(cacheDir);
        _backingPath = Path.Combine(cacheDir, HashKey(key) + ".dat");

        _handle = File.OpenHandle(
            _backingPath, FileMode.Create, FileAccess.ReadWrite, FileShare.None,
            FileOptions.RandomAccess);

        if (createEmpty)
        {
            // A brand-new object: there is no previous version to copy parts from.
            _remoteLength = 0;
            _length = 0;
            _fullUploadRequired = true;
        }
        else
        {
            RandomAccess.SetLength(_handle, remoteSize);
        }
    }

    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private long ChunkOf(long offset) => offset / _chunkSize;

    private bool IsPresent(long chunk)
    {
        lock (_presentGate) return _present.Contains(chunk);
    }

    private void MarkPresent(long chunk)
    {
        lock (_presentGate) _present.Add(chunk);
    }

    // ------------------------------------------------------------------ hydration

    /// <summary>Downloads any chunk overlapping [offset, offset+count) that is not cached yet.</summary>
    private void Hydrate(long offset, long count)
    {
        if (_remoteLength <= 0 || count <= 0) return;

        var last = Math.Min(_remoteLength - 1, offset + count - 1);
        if (last < offset) return;

        var missing = new List<long>();
        for (var chunk = ChunkOf(offset); chunk <= ChunkOf(last); chunk++)
            if (!IsPresent(chunk)) missing.Add(chunk);

        if (missing.Count == 0) return;

        // Many small concurrent range GETs beat one sequential stream against Wasabi by a wide
        // margin; this mirrors rclone's --vfs-read-chunk-streams.
        Parallel.ForEach(
            missing,
            new ParallelOptions { MaxDegreeOfParallelism = _maxStreams },
            chunk =>
            {
                FetchChunk(chunk);
                MarkPresent(chunk);
            });
    }

    private void FetchChunk(long chunk)
    {
        var start = chunk * _chunkSize;
        var length = (int)Math.Min(_chunkSize, _remoteLength - start);
        if (length <= 0) return;

        var buffer = new byte[length];
        using var stream = _s3.OpenReadAsync(_key, start, length).GetAwaiter().GetResult();

        var read = 0;
        while (read < length)
        {
            var n = stream.Read(buffer, read, length - read);
            if (n <= 0) break;
            read += n;
        }

        RandomAccess.Write(_handle, buffer.AsSpan(0, read), start);
    }

    // ------------------------------------------------------------------ data

    public int Read(long offset, byte[] buffer, int count)
    {
        var length = Length;
        if (offset >= length) return 0;

        var toRead = (int)Math.Min(count, length - offset);
        Hydrate(offset, toRead);
        return RandomAccess.Read(_handle, buffer.AsSpan(0, toRead), offset);
    }

    public int Write(long offset, byte[] data, int count)
    {
        // Read-modify-write: a chunk straddling the written range must be hydrated first, or the
        // eventual upload of that part would ship uninitialised zeroes around the new bytes.
        if (offset < _remoteLength) Hydrate(offset, count);

        var tail = offset + count;
        if (tail < _remoteLength) Hydrate(tail, 1);

        RandomAccess.Write(_handle, data.AsSpan(0, count), offset);

        for (var chunk = ChunkOf(offset); chunk <= ChunkOf(Math.Max(offset, tail - 1)); chunk++)
            MarkPresent(chunk);

        lock (_dirtyGate) _dirtyRanges.Add(offset, tail);

        var current = Interlocked.Read(ref _length);
        while (tail > current)
        {
            var previous = Interlocked.CompareExchange(ref _length, tail, current);
            if (previous == current) break;
            current = previous;
        }

        return count;
    }

    public void SetLength(long newLength)
    {
        _flushGate.Wait();
        try
        {
            var old = Interlocked.Read(ref _length);
            RandomAccess.SetLength(_handle, newLength);
            Interlocked.Exchange(ref _length, newLength);

            if (newLength > old)
            {
                // The file grew: the new tail is local zeroes, which no previous version can supply.
                lock (_dirtyGate) _dirtyRanges.Add(old, newLength);
                for (var chunk = ChunkOf(old); chunk <= ChunkOf(newLength - 1); chunk++)
                    MarkPresent(chunk);
            }
            else if (newLength < old)
            {
                lock (_dirtyGate) _dirtyRanges.ClipTo(newLength);
                lock (_presentGate) _present.RemoveWhere(c => c * _chunkSize >= newLength);
            }

            _lengthChanged = true;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    /// <summary>Truncates to zero — the Overwrite path. No previous bytes remain reusable.</summary>
    public void Truncate()
    {
        _flushGate.Wait();
        try
        {
            RandomAccess.SetLength(_handle, 0);
            Interlocked.Exchange(ref _length, 0);
            _remoteLength = 0;
            lock (_presentGate) _present.Clear();
            lock (_dirtyGate) _dirtyRanges.Clear();
            _fullUploadRequired = true;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    // ------------------------------------------------------------------ upload

    /// <summary>Uploads what changed. Returns the new ETag, or null when there was nothing to do.</summary>
    public string? Flush()
    {
        _flushGate.Wait();
        try
        {
            if (!Dirty) return null;

            RandomAccess.FlushToDisk(_handle);
            var length = Length;

            long dirtyBytes;
            (long Start, long End)[] ranges;
            lock (_dirtyGate)
            {
                dirtyBytes = _dirtyRanges.TotalBytes;
                ranges = _dirtyRanges.Ranges.ToArray();
            }

            var partSize = SplicePlan.ChoosePartSize(length);
            var etag = SplicePlan.ShouldSplice(length, _remoteLength, dirtyBytes, _fullUploadRequired, partSize)
                ? Splice(length, partSize, ranges)
                : WholePut(length);

            _remoteLength = length;
            _fullUploadRequired = false;
            _lengthChanged = false;
            lock (_dirtyGate) _dirtyRanges.Clear();
            return etag;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private string? WholePut(long length)
    {
        // Everything changed (or the object is too small to split), so one PUT is the cheapest path.
        if (_remoteLength > 0 && length > 0) Hydrate(0, length);
        return _s3.PutObjectAsync(_key, _backingPath).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Rebuilds the object from its own previous version plus the changed parts. Dirty ranges are
    /// expanded to the part grid so every part is wholly changed or wholly untouched; changed
    /// parts upload from the backing file, untouched parts are copied server-side.
    /// </summary>
    private string? Splice(long length, long partSize, (long Start, long End)[] dirtyRanges)
    {
        var plan = SplicePlan.Plan(length, partSize, dirtyRanges);
        var uploadId = _s3.BeginMultipartUploadAsync(_key).GetAwaiter().GetResult();

        try
        {
            var completed = new ConcurrentBag<S3CompletedPart>();
            long uploadedBytes = 0;

            Parallel.ForEach(
                plan,
                new ParallelOptions { MaxDegreeOfParallelism = _maxStreams },
                part =>
                {
                    if (part.Dirty)
                    {
                        // The part's bytes that still live only in S3 must come local first.
                        if (part.Start < _remoteLength)
                            Hydrate(part.Start, Math.Min(part.End, _remoteLength) - part.Start);

                        completed.Add(_s3.UploadPartFromFileAsync(
                            _key, uploadId, part.PartNumber, _backingPath, part.Start, part.Length)
                            .GetAwaiter().GetResult());

                        Interlocked.Add(ref uploadedBytes, part.Length);
                    }
                    else
                    {
                        // Untouched: copy it out of the version we are about to replace. A clean
                        // part always sits below _remoteLength, because anything past the old end
                        // of the object was necessarily written locally.
                        completed.Add(_s3.CopyPartAsync(
                            _key, uploadId, part.PartNumber, _key, part.Start, part.End - 1)
                            .GetAwaiter().GetResult());
                    }
                });

            var etag = _s3.CompleteMultipartUploadAsync(_key, uploadId, completed)
                .GetAwaiter().GetResult();

            _log?.Invoke(
                $"Spliced '{_key}': uploaded {Mib(uploadedBytes)} of {Mib(length)} " +
                $"({plan.Count(p => p.Dirty)}/{plan.Count} parts; " +
                $"{Mib(length - uploadedBytes)} copied server-side).");

            return etag;
        }
        catch
        {
            // Wasabi bills for the parts of an upload that is neither completed nor aborted.
            try { _s3.AbortMultipartUploadAsync(_key, uploadId).GetAwaiter().GetResult(); }
            catch (Exception ex) { _log?.Invoke($"Abort of '{_key}' failed: {ex.Message}"); }
            throw;
        }
    }

    private static string Mib(long bytes) => $"{bytes / 1024.0 / 1024.0:N1} MiB";

    public void Dispose()
    {
        _handle.Dispose();
        _flushGate.Dispose();
        try { if (File.Exists(_backingPath)) File.Delete(_backingPath); }
        catch { /* the cache sweeper can pick it up later */ }
    }
}
