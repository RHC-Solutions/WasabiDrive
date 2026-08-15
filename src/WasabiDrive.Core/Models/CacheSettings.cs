using System.Text.Json.Serialization;
using System.Xml.Serialization;

namespace WasabiDrive.Core.Models;

/// <summary>rclone VFS cache modes. See https://rclone.org/commands/rclone_mount/#vfs-file-caching</summary>
public enum VfsCacheMode
{
    Off,
    Minimal,
    Writes,
    Full,
}

/// <summary>
/// rclone VFS/cache tuning knobs surfaced in the UI and translated to mount flags
/// by <see cref="RcloneRunner"/>.
/// </summary>
public sealed class CacheSettings
{
    public VfsCacheMode CacheMode { get; set; } = VfsCacheMode.Full;

    /// <summary>Max on-disk cache size in MiB. 0 = unlimited (omit the flag). Default 50 GiB.</summary>
    public int VfsCacheMaxSizeMb { get; set; } = 50 * 1024;

    /// <summary>
    /// Objects are evicted from the cache after this idle age. Default 24h so a large cache
    /// actually keeps recently-used files around rather than dropping them after an hour.
    /// </summary>
    [XmlIgnore]
    public TimeSpan VfsCacheMaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>XML-serialization surrogate for <see cref="VfsCacheMaxAge"/> (TimeSpan isn't XML-serializable).</summary>
    [JsonIgnore]
    [XmlElement("VfsCacheMaxAgeSeconds")]
    public long VfsCacheMaxAgeSeconds
    {
        get => (long)VfsCacheMaxAge.TotalSeconds;
        set => VfsCacheMaxAge = TimeSpan.FromSeconds(value);
    }

    /// <summary>
    /// How long directory listings are cached before re-reading from Wasabi. Wasabi/S3 cannot push
    /// change notifications, so changes made by other tools (the Wasabi console, Wasabi Commander)
    /// only appear on the drive after this interval. Kept short (1 min) so external changes surface
    /// quickly; raise it to cut S3 LIST requests if you don't edit the bucket elsewhere.
    ///
    /// IMPORTANT: this must exceed the time it takes to enumerate the bucket's largest directory.
    /// A listing that outlives its own cache entry is discarded before it can be served, so rclone
    /// restarts it forever and the drive never opens. A bucket whose root holds ~800k objects with
    /// no common prefixes takes 10+ minutes to list, which the 1-minute default cannot survive.
    /// </summary>
    [XmlIgnore]
    public TimeSpan DirCacheTime { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>XML-serialization surrogate for <see cref="DirCacheTime"/>.</summary>
    [JsonIgnore]
    [XmlElement("DirCacheTimeSeconds")]
    public long DirCacheTimeSeconds
    {
        get => (long)DirCacheTime.TotalSeconds;
        set => DirCacheTime = TimeSpan.FromSeconds(value);
    }

    /// <summary>In-memory read-ahead buffer per open file, in MiB.</summary>
    public int BufferSizeMb { get; set; } = 16;

    /// <summary>
    /// Extra sequential read-ahead beyond <see cref="BufferSizeMb"/>, in MiB
    /// (<c>--vfs-read-ahead</c>, cache-mode Full only). 0 = omit the flag.
    /// </summary>
    public int ReadAheadMb { get; set; } = 128;

    /// <summary>
    /// Parallel download streams per open file (<c>--vfs-read-chunk-streams</c>). Wasabi is a
    /// high-performance object store, so many small concurrent range GETs beat one sequential
    /// stream by a wide margin. 0 = rclone's default (one stream with a doubling chunk size).
    /// </summary>
    public int ReadChunkStreams { get; set; } = 16;

    /// <summary>
    /// Size of each parallel read chunk in MiB (<c>--vfs-read-chunk-size</c>). Small chunks are
    /// correct when <see cref="ReadChunkStreams"/> is high — rclone's own S3 guidance is
    /// 16 streams × 4 MiB. 0 = omit the flag.
    /// </summary>
    public int ReadChunkSizeMb { get; set; } = 4;

    /// <summary>
    /// How many files upload from the cache to Wasabi at once (<c>--transfers</c>). The main lever
    /// for copying many small files, where per-object latency dominates.
    /// </summary>
    public int Transfers { get; set; } = 8;

    /// <summary>
    /// Parallel multipart chunks within a single large upload (<c>--s3-upload-concurrency</c>).
    /// </summary>
    public int UploadConcurrency { get; set; } = 4;

    /// <summary>
    /// Multipart upload chunk size in MiB (<c>--s3-chunk-size</c>); rclone's default of 5 MiB
    /// makes for a lot of round trips on large files. Worst-case upload buffer memory is
    /// <see cref="Transfers"/> × <see cref="UploadConcurrency"/> × this value, so raise it with care.
    /// </summary>
    public int UploadChunkSizeMb { get; set; } = 16;

    /// <summary>
    /// Take modification times from the S3 object's <c>LastModified</c> (free, comes back with the
    /// directory listing) instead of the per-object metadata, which costs an extra HEAD request per
    /// file (<c>--use-server-modtime</c>). Timestamps then reflect upload time rather than the
    /// original file's mtime.
    /// </summary>
    public bool UseServerModTime { get; set; } = true;

    /// <summary>
    /// Detect changes from size + modtime instead of hashing (<c>--vfs-fast-fingerprint</c>),
    /// avoiding extra requests when the VFS revalidates a cached file.
    /// </summary>
    public bool FastFingerprint { get; set; } = true;

    /// <summary>
    /// Directory where rclone stores the on-disk VFS cache (<c>--cache-dir</c>). Null/blank =
    /// rclone's default (<c>%LOCALAPPDATA%\rclone</c>). Point this at a roomy drive when using a
    /// large cache size.
    /// </summary>
    public string? CacheDir { get; set; }

    public static CacheSettings Default() => new();

    public CacheSettings Clone() => new()
    {
        CacheMode = CacheMode,
        VfsCacheMaxSizeMb = VfsCacheMaxSizeMb,
        VfsCacheMaxAge = VfsCacheMaxAge,
        DirCacheTime = DirCacheTime,
        BufferSizeMb = BufferSizeMb,
        ReadAheadMb = ReadAheadMb,
        ReadChunkStreams = ReadChunkStreams,
        ReadChunkSizeMb = ReadChunkSizeMb,
        Transfers = Transfers,
        UploadConcurrency = UploadConcurrency,
        UploadChunkSizeMb = UploadChunkSizeMb,
        UseServerModTime = UseServerModTime,
        FastFingerprint = FastFingerprint,
        CacheDir = CacheDir,
    };
}
