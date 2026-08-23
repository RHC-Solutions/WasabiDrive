using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using WasabiDrive.Core.Bulk;

namespace WasabiDrive.CloudFiles;

/// <summary>
/// Bulk folder operations executed against the S3 API instead of through the mounted drive.
///
/// A recursive delete or move on the drive letter is unusable at scale, and not because rclone is
/// slow: the WinFsp/FUSE contract delivers one unlink per file and one synchronous rename per
/// entry, so Explorer blocks its UI thread on thousands of sequential round-trips with no progress
/// and no cancel, and the 1000-keys-per-request DeleteObjects call can never be reached from in
/// there. Running the same work here turns a 10,000-object folder delete from thousands of
/// requests into ten, keeps the UI responsive, and makes cancellation real.
///
/// <see cref="WasabiS3Client.DeleteObjectsAsync"/> already batches, but it runs its batches one
/// after another and reports nothing until it finishes. This class adds what a foreground,
/// user-facing operation needs on top: streaming enumeration, concurrency, live progress and
/// cancellation. Listing runs concurrently with the work, so nothing waits for a full enumeration
/// of the bucket and memory stays flat over hundreds of thousands of keys.
///
/// Cancelling is safe. Every object is copied before its source is deleted, so a cancelled move
/// leaves each object either at its source or at its destination -- at worst a copy whose source
/// deletion had not run yet, which re-running the move cleans up. Nothing is lost.
///
/// After any of these run, the mount's directory cache is stale and Explorer keeps showing the
/// old tree until it expires (see the mapping's dir-cache-time). Refresh the mount, or drive
/// rclone's rc API (vfs/forget) for the affected prefix, once the operation reports Completed.
/// </summary>
public sealed class S3BulkOperations
{
    private readonly WasabiS3Client _s3;
    private readonly BulkOptions _options;
    private readonly Action<string>? _log;

    public S3BulkOperations(WasabiS3Client s3, BulkOptions? options = null, Action<string>? log = null)
    {
        _s3 = s3 ?? throw new ArgumentNullException(nameof(s3));
        _options = options ?? BulkOptions.Default;
        _options.Validate();
        _log = log;
    }

    /// <summary>
    /// Deletes every object under <paramref name="prefix"/> (null or empty = the whole bucket),
    /// batching keys into the largest requests S3 allows. The 0-byte directory markers under the
    /// prefix are ordinary keys and go with it, so the folder itself disappears too.
    /// </summary>
    public async Task<BulkResult> DeletePrefixAsync(
        string? prefix,
        IProgress<BulkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var normalized = BulkKeyMapper.NormalizePrefix(prefix);
        var tally = new Tally();
        var pump = new ProgressPump(progress, _options.ProgressInterval);
        var keys = CreateKeyChannel();
        var deleter = DrainDeletesAsync(keys.Reader, tally, pump, ct);
        var canceled = false;

        try
        {
            var listing = Counted(_s3.ListObjectsAsync(normalized, ct), tally, pump, BulkPhase.Deleting, ct);
            await foreach (var entry in listing.ConfigureAwait(false))
                await keys.Writer.WriteAsync(entry.Key, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            keys.Writer.TryComplete();
        }

        canceled |= await AwaitDrainAsync(deleter).ConfigureAwait(false);
        pump.ReportFinal(tally);
        return tally.ToResult(canceled);
    }

    /// <summary>
    /// Deletes an explicit set of keys -- the multi-selection case, where Explorer would otherwise
    /// issue one unlink per file.
    /// </summary>
    public async Task<BulkResult> DeleteKeysAsync(
        IEnumerable<string> keys,
        IProgress<BulkProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);

        var tally = new Tally();
        var pump = new ProgressPump(progress, _options.ProgressInterval);
        var channel = CreateKeyChannel();
        var deleter = DrainDeletesAsync(channel.Reader, tally, pump, ct);
        var canceled = false;

        try
        {
            foreach (var key in keys)
            {
                tally.AddFound(1, 0);
                await channel.Writer.WriteAsync(key, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            channel.Writer.TryComplete();
        }

        canceled |= await AwaitDrainAsync(deleter).ConfigureAwait(false);
        pump.ReportFinal(tally);
        return tally.ToResult(canceled);
    }

    /// <summary>
    /// Moves everything under <paramref name="sourcePrefix"/> to <paramref name="destPrefix"/>.
    /// S3 has no rename, so each object is copied server-side -- the bytes never travel through
    /// this machine -- and its source key is deleted only once its copy has succeeded. Copies are
    /// fanned out <see cref="BulkOptions.CopyConcurrency"/> wide and the resulting deletions are
    /// batched, so the two stages overlap instead of running end to end.
    /// </summary>
    public async Task<BulkResult> MovePrefixAsync(
        string? sourcePrefix,
        string? destPrefix,
        IProgress<BulkProgress>? progress = null,
        CancellationToken ct = default)
    {
        var (source, destination) = BulkKeyMapper.ValidateMove(sourcePrefix, destPrefix);

        var tally = new Tally();
        var pump = new ProgressPump(progress, _options.ProgressInterval);
        var deletions = CreateKeyChannel();
        var deleter = DrainDeletesAsync(deletions.Reader, tally, pump, ct);
        var canceled = false;

        try
        {
            var listing = Counted(_s3.ListObjectsAsync(source, ct), tally, pump, BulkPhase.Copying, ct);
            var parallelism = new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.CopyConcurrency,
                CancellationToken = ct,
            };

            await Parallel.ForEachAsync(listing, parallelism, async (entry, token) =>
            {
                var destKey = BulkKeyMapper.MapKey(entry.Key, source, destination);
                try
                {
                    // The size comes from the listing, so the copy costs exactly one request even
                    // when it has to take the multipart path.
                    await S3Retry.RunAsync(
                        () => _s3.CopyObjectAsync(entry.Key, destKey, entry.Size, token),
                        _log, _options.RetryAttempts).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One object failing must not abandon the rest of the move; its source stays
                    // in place so a re-run picks it up.
                    tally.AddFailure(new BulkFailure(entry.Key, ex.Message));
                    return;
                }

                tally.AddCopied(entry.Size);
                pump.Report(BulkPhase.Copying, tally, entry.Key);
                await deletions.Writer.WriteAsync(entry.Key, token).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            canceled = true;
        }
        finally
        {
            deletions.Writer.TryComplete();
        }

        canceled |= await AwaitDrainAsync(deleter).ConfigureAwait(false);
        pump.ReportFinal(tally);
        return tally.ToResult(canceled);
    }

    // ---- plumbing --------------------------------------------------------------------------

    /// <summary>
    /// Bounded so a fast lister cannot race ahead of the deleters and buffer the whole bucket in
    /// memory; the write back-pressures instead.
    /// </summary>
    private Channel<string> CreateKeyChannel() =>
        Channel.CreateBounded<string>(new BoundedChannelOptions(
            _options.DeleteBatchSize * _options.DeleteConcurrency * 2)
        {
            SingleReader = true,
        });

    /// <summary>Counts objects as they stream past, so progress reflects real listing depth.</summary>
    private static async IAsyncEnumerable<S3ObjectEntry> Counted(
        IAsyncEnumerable<S3ObjectEntry> source,
        Tally tally,
        ProgressPump pump,
        BulkPhase phase,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in source.WithCancellation(ct).ConfigureAwait(false))
        {
            tally.AddFound(1, entry.Size);
            pump.Report(phase, tally, entry.Key);
            yield return entry;
        }
    }

    /// <summary>
    /// Consumes keys and issues batched DeleteObjects requests. Each pass takes everything
    /// currently buffered, up to a full batch: a fast producer yields full 1000-key requests,
    /// while a slow one (the copy stage of a move) still gets its deletions out promptly instead
    /// of waiting for a batch that may never fill. Batches are capped at the S3 per-request limit
    /// so each call to the client is exactly one round-trip.
    /// </summary>
    private async Task DrainDeletesAsync(
        ChannelReader<string> reader, Tally tally, ProgressPump pump, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(_options.DeleteConcurrency);
        var inflight = new List<Task>();

        async Task FlushAsync(List<string> batch)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var result = await S3Retry.RunAsync(
                    () => _s3.DeleteObjectsAsync(batch, ct),
                    _log, _options.RetryAttempts).ConfigureAwait(false);

                tally.AddDeleted(result.Deleted.Count);
                tally.AddFailures(result.Failed.Select(
                    k => new BulkFailure(k, "the server rejected the delete")));
                pump.Report(BulkPhase.Deleting, tally, batch[^1]);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                tally.AddFailures(batch.Select(k => new BulkFailure(k, ex.Message)));
            }
            finally
            {
                gate.Release();
            }
        }

        while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var batch = new List<string>(_options.DeleteBatchSize);
            while (batch.Count < _options.DeleteBatchSize && reader.TryRead(out var key))
                batch.Add(key);

            if (batch.Count > 0)
                inflight.Add(FlushAsync(batch));

            inflight.RemoveAll(t => t.IsCompleted);
        }

        await Task.WhenAll(inflight).ConfigureAwait(false);
    }

    /// <summary>Awaits the delete pump, turning cancellation into a flag rather than a throw.</summary>
    private static async Task<bool> AwaitDrainAsync(Task deleter)
    {
        try
        {
            await deleter.ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>Thread-safe counters shared by the listing, copy and delete stages.</summary>
    private sealed class Tally
    {
        private readonly ConcurrentQueue<BulkFailure> _failures = new();
        private long _found, _bytesFound, _copied, _bytesCopied, _deleted;

        public long Found => Interlocked.Read(ref _found);
        public long BytesFound => Interlocked.Read(ref _bytesFound);
        public long Copied => Interlocked.Read(ref _copied);
        public long BytesCopied => Interlocked.Read(ref _bytesCopied);
        public long Deleted => Interlocked.Read(ref _deleted);

        public void AddFound(long count, long bytes)
        {
            Interlocked.Add(ref _found, count);
            Interlocked.Add(ref _bytesFound, bytes);
        }

        public void AddCopied(long bytes)
        {
            Interlocked.Increment(ref _copied);
            Interlocked.Add(ref _bytesCopied, bytes);
        }

        public void AddDeleted(long count) => Interlocked.Add(ref _deleted, count);

        public void AddFailure(BulkFailure failure) => _failures.Enqueue(failure);

        public void AddFailures(IEnumerable<BulkFailure> failures)
        {
            foreach (var failure in failures)
                _failures.Enqueue(failure);
        }

        public BulkResult ToResult(bool canceled) =>
            new(Copied, Deleted, BytesCopied, _failures.ToArray(), canceled);
    }

    /// <summary>
    /// Throttles progress callbacks. A 500k-object delete would otherwise marshal half a million
    /// updates onto the UI thread and re-freeze the very Explorer this class exists to unblock.
    /// </summary>
    private sealed class ProgressPump
    {
        private readonly IProgress<BulkProgress>? _sink;
        private readonly long _intervalMs;
        private long _lastReportMs = long.MinValue;

        public ProgressPump(IProgress<BulkProgress>? sink, TimeSpan interval)
        {
            _sink = sink;
            _intervalMs = (long)interval.TotalMilliseconds;
        }

        public void Report(BulkPhase phase, Tally tally, string? currentKey)
        {
            if (_sink is null)
                return;

            var now = Environment.TickCount64;
            var last = Interlocked.Read(ref _lastReportMs);
            if (now - last < _intervalMs)
                return;
            // Lost the race to another thread; it just reported, so skip this one.
            if (Interlocked.CompareExchange(ref _lastReportMs, now, last) != last)
                return;

            Emit(phase, tally, currentKey);
        }

        public void ReportFinal(Tally tally) => Emit(BulkPhase.Completed, tally, null);

        private void Emit(BulkPhase phase, Tally tally, string? currentKey) =>
            _sink?.Report(new BulkProgress(
                phase,
                tally.Found,
                phase == BulkPhase.Copying ? tally.Copied : tally.Deleted,
                tally.BytesFound,
                tally.BytesCopied,
                currentKey));
    }
}
