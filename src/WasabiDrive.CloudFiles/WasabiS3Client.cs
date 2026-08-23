using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using WasabiDrive.Core.Models;

namespace WasabiDrive.CloudFiles;

/// <summary>One object listed under a bucket/prefix.</summary>
/// <param name="Key">Full S3 key.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp (UTC).</param>
/// <param name="ETag">Object ETag (used to detect remote changes).</param>
public sealed record S3ObjectEntry(string Key, long Size, DateTime LastModifiedUtc, string? ETag);

/// <summary>
/// Thin wrapper over the AWS S3 SDK pointed at a Wasabi region endpoint. Used by the Cloud Files
/// provider to enumerate objects (to build placeholders) and to range-read object bytes on
/// hydration. Read-only for the one-way milestone.
/// </summary>
public sealed class WasabiS3Client : IDisposable
{
    /// <summary>Files at or above this size upload as a concurrent multipart transfer.</summary>
    private const long MultipartThresholdBytes = 16L * 1024 * 1024;

    /// <summary>Size of each multipart chunk. Peak upload memory is this × <see cref="UploadConcurrency"/>.</summary>
    private const long MultipartPartSizeBytes = 16L * 1024 * 1024;

    /// <summary>Parallel part uploads within one file.</summary>
    private const int UploadConcurrency = 8;

    /// <summary>S3 caps a single DeleteObjects request at 1000 keys.</summary>
    private const int DeleteBatchSize = 1000;

    private readonly IAmazonS3 _s3;
    private readonly TransferUtility _transfer;
    private readonly string _bucket;

    public WasabiS3Client(string endpointHost, string regionCode, string accessKeyId, string secretAccessKey, string bucket)
    {
        if (string.IsNullOrWhiteSpace(endpointHost)) throw new ArgumentException("Endpoint is required.", nameof(endpointHost));
        if (string.IsNullOrWhiteSpace(bucket)) throw new ArgumentException("Bucket is required.", nameof(bucket));

        _bucket = bucket;
        var config = new AmazonS3Config
        {
            ServiceURL = "https://" + endpointHost.TrimEnd('/'),
            // Wasabi is S3-compatible; path-style avoids DNS/cert issues with dotted bucket names.
            ForcePathStyle = true,
            AuthenticationRegion = regionCode,
        };
        _s3 = new AmazonS3Client(new BasicAWSCredentials(accessKeyId, secretAccessKey), config);
        _transfer = new TransferUtility(_s3, new TransferUtilityConfig
        {
            ConcurrentServiceRequests = UploadConcurrency,
            MinSizeBeforePartUpload = MultipartThresholdBytes,
        });
    }

    /// <summary>Builds a client for the given mapping + credentials.</summary>
    public static WasabiS3Client ForMapping(Mapping mapping, WasabiCredentials creds)
    {
        var region = WasabiRegion.FindByCode(mapping.RegionCode)
            ?? throw new InvalidOperationException($"Unknown region '{mapping.RegionCode}'.");
        return new WasabiS3Client(region.Endpoint, mapping.RegionCode,
            creds.AccessKeyId, creds.SecretAccessKey, mapping.BucketName);
    }

    /// <summary>Enumerates every object under <paramref name="prefix"/> (paginated).</summary>
    public async IAsyncEnumerable<S3ObjectEntry> ListObjectsAsync(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new ListObjectsV2Request
        {
            BucketName = _bucket,
            Prefix = string.IsNullOrWhiteSpace(prefix) ? null : prefix,
            MaxKeys = 1000,
        };

        ListObjectsV2Response response;
        do
        {
            response = await _s3.ListObjectsV2Async(request, ct).ConfigureAwait(false);
            foreach (var o in response.S3Objects)
                yield return new S3ObjectEntry(o.Key, o.Size, o.LastModified.ToUniversalTime(), o.ETag);
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (response.IsTruncated);
    }

    /// <summary>
    /// Opens a read stream for a byte range of an object (used to feed hydration). When
    /// <paramref name="length"/> is null the whole object from <paramref name="offset"/> is read.
    /// </summary>
    public async Task<Stream> OpenReadAsync(string key, long offset, long? length, CancellationToken ct = default)
    {
        var request = new GetObjectRequest { BucketName = _bucket, Key = key };
        if (offset > 0 || length is not null)
        {
            var end = length is null ? "" : (offset + length.Value - 1).ToString();
            request.ByteRange = new ByteRange($"bytes={offset}-{end}");
        }
        var response = await _s3.GetObjectAsync(request, ct).ConfigureAwait(false);
        return response.ResponseStream;
    }

    /// <summary>Uploads a local file to <paramref name="key"/> (overwrites). Returns the new ETag.</summary>
    public async Task<string?> PutObjectAsync(string key, string localPath, CancellationToken ct = default)
    {
        // A single PUT means one serial stream, which leaves most of the link idle on big files.
        // Above the threshold, hand off to a concurrent multipart transfer instead.
        long length;
        try { length = new FileInfo(localPath).Length; }
        catch { length = 0; }

        if (length >= MultipartThresholdBytes)
        {
            await _transfer.UploadAsync(new TransferUtilityUploadRequest
            {
                BucketName = _bucket,
                Key = key,
                FilePath = localPath,
                PartSize = MultipartPartSizeBytes,
                DisablePayloadSigning = true,
            }, ct).ConfigureAwait(false);

            // TransferUtility doesn't surface the CompleteMultipartUpload response, and the caller
            // needs the real ETag: the pull reconcile compares it against the remote one to decide
            // whether an object changed, so a missing ETag would make every large upload look like
            // a remote change and pull the file straight back down. One HEAD is cheap next to a
            // multipart upload.
            var head = await _s3.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = _bucket, Key = key }, ct).ConfigureAwait(false);
            return head.ETag;
        }

        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = localPath,
            DisablePayloadSigning = true, // large-file friendly against S3-compatible endpoints
        };
        var response = await _s3.PutObjectAsync(request, ct).ConfigureAwait(false);
        return response.ETag;
    }

    /// <summary>Deletes an object.</summary>
    public async Task DeleteObjectAsync(string key, CancellationToken ct = default) =>
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct)
            .ConfigureAwait(false);

    /// <summary>The outcome of a batched delete: which keys went, and which the server rejected.</summary>
    public sealed record DeleteResult(IReadOnlyList<string> Deleted, IReadOnlyList<string> Failed);

    /// <summary>
    /// Deletes many objects using batched DeleteObjects requests (up to 1000 keys each) instead of
    /// one round trip per key — the difference between one request and a thousand when a folder goes.
    /// Partial failures are reported rather than thrown, so the keys that did go can be forgotten
    /// while the ones that didn't stay tracked.
    /// </summary>
    public async Task<DeleteResult> DeleteObjectsAsync(
        IEnumerable<string> keys, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var deleted = new List<string>();
        var failed = new List<string>();

        foreach (var batch in keys.Distinct(StringComparer.Ordinal).Chunk(DeleteBatchSize))
        {
            // Quiet keeps the response to just the failures, so a 1000-key delete doesn't ship a
            // large success payload back. Note the SDK signals per-key failures by throwing
            // DeleteObjectsException rather than returning them, so both paths are handled.
            var request = new DeleteObjectsRequest
            {
                BucketName = _bucket,
                Objects = batch.Select(k => new KeyVersion { Key = k }).ToList(),
                Quiet = true,
            };

            try
            {
                var response = await _s3.DeleteObjectsAsync(request, ct).ConfigureAwait(false);
                Record(batch, response.DeleteErrors);
            }
            catch (DeleteObjectsException ex)
            {
                // Some keys in this batch failed; the others were still removed.
                Record(batch, ex.Response.DeleteErrors);
            }
        }

        return new DeleteResult(deleted, failed);

        void Record(string[] batch, List<DeleteError>? errors)
        {
            var bad = errors?.Select(e => e.Key).ToHashSet(StringComparer.Ordinal)
                      ?? new HashSet<string>(StringComparer.Ordinal);
            deleted.AddRange(batch.Where(k => !bad.Contains(k)));
            failed.AddRange(batch.Where(bad.Contains));
        }
    }

    /// <summary>A single CopyObject cannot exceed 5 GiB; past that the copy must be multipart.</summary>
    private const long SingleCopyLimitBytes = 5L * 1024 * 1024 * 1024;

    /// <summary>Part size for multipart copies: 512 MiB x 10,000 parts covers any legal object.</summary>
    private const long CopyPartSizeBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Server-side copy of one object. Nothing is downloaded — the bytes never leave Wasabi.
    /// Above the 5 GiB single-call limit this switches to a multipart copy; pass
    /// <paramref name="size"/> when it is already known (a listing gives it for free) to avoid the
    /// HEAD request that otherwise has to establish which path to take.
    /// </summary>
    public async Task CopyObjectAsync(
        string sourceKey, string destKey, long? size = null, CancellationToken ct = default)
    {
        var bytes = size ?? (await _s3.GetObjectMetadataAsync(
            new GetObjectMetadataRequest { BucketName = _bucket, Key = sourceKey }, ct)
            .ConfigureAwait(false)).ContentLength;

        if (bytes <= SingleCopyLimitBytes)
        {
            await _s3.CopyObjectAsync(new CopyObjectRequest
            {
                SourceBucket = _bucket,
                SourceKey = sourceKey,
                DestinationBucket = _bucket,
                DestinationKey = destKey,
            }, ct).ConfigureAwait(false);
            return;
        }

        await CopyLargeObjectAsync(sourceKey, destKey, bytes, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Multipart server-side copy for objects over 5 GiB. Parts are copied in order; on any
    /// failure the upload is aborted so Wasabi does not keep billing for orphaned parts.
    /// </summary>
    private async Task CopyLargeObjectAsync(string sourceKey, string destKey, long size, CancellationToken ct)
    {
        var initiated = await _s3.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = _bucket,
            Key = destKey,
        }, ct).ConfigureAwait(false);

        try
        {
            var parts = new List<PartETag>();
            long position = 0;
            for (var partNumber = 1; position < size; partNumber++)
            {
                var lastByte = Math.Min(position + CopyPartSizeBytes, size) - 1;
                var part = await _s3.CopyPartAsync(new CopyPartRequest
                {
                    SourceBucket = _bucket,
                    SourceKey = sourceKey,
                    DestinationBucket = _bucket,
                    DestinationKey = destKey,
                    UploadId = initiated.UploadId,
                    PartNumber = partNumber,
                    FirstByte = position,
                    LastByte = lastByte,
                }, ct).ConfigureAwait(false);

                parts.Add(new PartETag(partNumber, part.ETag));
                position = lastByte + 1;
            }

            await _s3.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = _bucket,
                Key = destKey,
                UploadId = initiated.UploadId,
                PartETags = parts,
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort, and deliberately not on `ct`: if we got here by cancellation that token
            // is already tripped, and the abort is exactly what still needs to run.
            try
            {
                await _s3.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
                {
                    BucketName = _bucket,
                    Key = destKey,
                    UploadId = initiated.UploadId,
                }, CancellationToken.None).ConfigureAwait(false);
            }
            catch { /* the orphaned upload also ages out via the bucket lifecycle rule */ }
            throw;
        }
    }

    /// <summary>Server-side copy then delete of the source (an S3 "rename").</summary>
    public async Task MoveObjectAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        await CopyObjectAsync(sourceKey, destKey, size: null, ct).ConfigureAwait(false);
        await DeleteObjectAsync(sourceKey, ct).ConfigureAwait(false);
    }

    /// <summary>Creates a time-limited presigned GET URL for sharing an object.</summary>
    public string GetPresignedUrl(string key, TimeSpan expiresIn)
    {
        return _s3.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiresIn),
        });
    }

    public void Dispose()
    {
        _transfer.Dispose();
        _s3.Dispose();
    }
}
