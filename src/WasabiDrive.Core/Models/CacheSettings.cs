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
        CacheDir = CacheDir,
    };
}
