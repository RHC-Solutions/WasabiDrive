namespace WasabiDrive.Core.Models;

/// <summary>Live mount state for a mapping.</summary>
public enum MountState
{
    Unmounted,
    Mounting,
    Mounted,
    Unmounting,
    Error,
}

/// <summary>How a bucket is surfaced to the user.</summary>
public enum MappingMode
{
    /// <summary>Virtual drive letter backed by rclone mount + WinFsp (the original behaviour).</summary>
    DriveLetter,

    /// <summary>
    /// A normal folder on disk with Windows "Files On-Demand" placeholders (Cloud Files API):
    /// files show in Explorer but download only when opened, with pin / free-up-space support.
    /// </summary>
    OnDemandFolder,
}

/// <summary>
/// A persisted bucket → drive-letter mapping. Contains no secret material; the matching
/// <see cref="WasabiCredentials"/> are looked up separately by <see cref="Id"/>.
/// </summary>
public sealed class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Friendly display name, e.g. "Backups".</summary>
    public string Name { get; set; } = string.Empty;

    public string BucketName { get; set; } = string.Empty;

    /// <summary>Drive letter without colon, e.g. "W".</summary>
    public string DriveLetter { get; set; } = "W";

    /// <summary>Wasabi region code, e.g. "us-east-1".</summary>
    public string RegionCode { get; set; } = "us-east-1";

    /// <summary>Optional path prefix within the bucket to mount as the drive root.</summary>
    public string? SubPath { get; set; }

    public bool AutoMount { get; set; }

    /// <summary>Whether this mapping is a drive-letter mount or an on-demand folder.</summary>
    public MappingMode Mode { get; set; } = MappingMode.DriveLetter;

    /// <summary>
    /// For <see cref="MappingMode.OnDemandFolder"/>: the local folder registered as a Cloud Files
    /// sync root. Null = a default under the user profile is used.
    /// </summary>
    public string? LocalFolderPath { get; set; }

    public CacheSettings Cache { get; set; } = CacheSettings.Default();

    /// <summary>The rclone remote target, e.g. "wasabi_&lt;id&gt;:bucket/subpath".</summary>
    public string RemoteName => "wasabi_" + Id.ToString("N");

    public string DriveTarget => DriveLetter.TrimEnd(':') + ":";

    public string RemoteTarget
    {
        get
        {
            var target = RemoteName + ":" + BucketName;
            if (!string.IsNullOrWhiteSpace(SubPath))
                target += "/" + SubPath!.Trim('/');
            return target;
        }
    }
}
