using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using WasabiDrive.Core.Models;

namespace WasabiDrive.CloudFiles;

/// <summary>One object listed under a bucket/prefix.</summary>
/// <param name="Key">Full S3 key.</param>
/// <param name="Size">Size in bytes.</param>
/// <param name="LastModifiedUtc">Last-modified timestamp (UTC).</param>
public sealed record S3ObjectEntry(string Key, long Size, DateTime LastModifiedUtc);

/// <summary>
/// Thin wrapper over the AWS S3 SDK pointed at a Wasabi region endpoint. Used by the Cloud Files
/// provider to enumerate objects (to build placeholders) and to range-read object bytes on
/// hydration. Read-only for the one-way milestone.
/// </summary>
public sealed class WasabiS3Client : IDisposable
{
    private readonly IAmazonS3 _s3;
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
                yield return new S3ObjectEntry(o.Key, o.Size, o.LastModified.ToUniversalTime());
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

    /// <summary>Uploads a local file to <paramref name="key"/> (overwrites).</summary>
    public async Task PutObjectAsync(string key, string localPath, CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = localPath,
            DisablePayloadSigning = true, // large-file friendly against S3-compatible endpoints
        };
        await _s3.PutObjectAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes an object.</summary>
    public async Task DeleteObjectAsync(string key, CancellationToken ct = default) =>
        await _s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = _bucket, Key = key }, ct)
            .ConfigureAwait(false);

    /// <summary>Server-side copy then delete of the source (an S3 "rename").</summary>
    public async Task MoveObjectAsync(string sourceKey, string destKey, CancellationToken ct = default)
    {
        await _s3.CopyObjectAsync(new CopyObjectRequest
        {
            SourceBucket = _bucket,
            SourceKey = sourceKey,
            DestinationBucket = _bucket,
            DestinationKey = destKey,
        }, ct).ConfigureAwait(false);
        await DeleteObjectAsync(sourceKey, ct).ConfigureAwait(false);
    }

    public void Dispose() => _s3.Dispose();
}
