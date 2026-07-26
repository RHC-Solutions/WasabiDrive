using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WasabiDrive.CloudFiles;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.ViewModels;

/// <summary>Backing model for the Add/Edit mapping dialog.</summary>
public sealed partial class MappingEditViewModel : ObservableObject
{
    private readonly Guid _id;

    /// <summary>Folders other mappings already occupy; two sync roots must not overlap.</summary>
    private readonly IReadOnlyList<string> _otherFolders;

    /// <summary>The folder this mapping was already registered at, if it is an existing on-demand one.</summary>
    private readonly string? _originalFolder;

    public MappingEditViewModel(Mapping? existing, WasabiCredentials? credentials, AppSettings settings,
        IEnumerable<string>? otherFolders = null)
    {
        _id = existing?.Id ?? Guid.NewGuid();
        IsNew = existing is null;
        _otherFolders = otherFolders?.ToList() ?? new List<string>();
        _originalFolder = existing?.Mode == MappingMode.OnDemandFolder
            ? OnDemandSyncManager.ResolveFolderPath(existing)
            : null;

        var m = existing ?? new Mapping { Cache = settings.DefaultCache.Clone() };
        Name = m.Name;
        BucketName = m.BucketName;
        SubPath = m.SubPath ?? string.Empty;
        RegionCode = m.RegionCode;
        AutoMount = m.AutoMount;
        // New mappings default to the on-demand folder (OneDrive/Google-Drive style); existing
        // mappings keep whatever mode they were saved with.
        Mode = existing?.Mode ?? MappingMode.OnDemandFolder;
        LocalFolderPath = m.LocalFolderPath ?? string.Empty;

        CacheMode = m.Cache.CacheMode;
        VfsCacheMaxSizeMb = m.Cache.VfsCacheMaxSizeMb;
        VfsCacheMaxAgeHours = m.Cache.VfsCacheMaxAge.TotalHours;
        DirCacheTimeMinutes = m.Cache.DirCacheTime.TotalMinutes;
        BufferSizeMb = m.Cache.BufferSizeMb;
        CacheDir = m.Cache.CacheDir ?? string.Empty;

        AccessKeyId = credentials?.AccessKeyId ?? string.Empty;
        SecretAccessKey = credentials?.SecretAccessKey ?? string.Empty;

        AvailableDriveLetters = BuildDriveLetters(m.DriveLetter);
        DriveLetter = string.IsNullOrWhiteSpace(m.DriveLetter) || !AvailableDriveLetters.Contains(m.DriveLetter)
            ? AvailableDriveLetters.FirstOrDefault() ?? "W"
            : m.DriveLetter;
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Add mapping" : "Edit mapping";

    public IReadOnlyList<WasabiRegion> Regions => WasabiRegion.All;
    public IReadOnlyList<VfsCacheMode> CacheModes { get; } =
        Enum.GetValues<VfsCacheMode>();
    public IReadOnlyList<MappingMode> Modes { get; } = Enum.GetValues<MappingMode>();
    public IReadOnlyList<string> AvailableDriveLetters { get; }

    /// <summary>True when the mapping uses a virtual drive letter (controls which fields show).</summary>
    public bool IsDriveLetterMode => Mode == MappingMode.DriveLetter;
    public bool IsOnDemandMode => Mode == MappingMode.OnDemandFolder;

    /// <summary>Helper text shown under the Mode dropdown explaining the selected mode.</summary>
    public string ModeDescription => Mode == MappingMode.OnDemandFolder
        ? "A normal folder in Explorer with cloud placeholders — files download only when opened, "
          + "with pin / free-up-space and no drive letter. Recommended."
        : "A virtual drive (e.g. W:) backed by rclone + WinFsp. The whole bucket appears as a mapped drive.";

    /// <summary>
    /// The folder that will actually be used, with the default filled in when no custom location is
    /// set. Shown in the dialog so the location is never a mystery — the same thing OneDrive does by
    /// always displaying its folder path rather than leaving the field blank.
    /// </summary>
    public string EffectiveFolderPath => OnDemandSyncManager.ResolveFolderPath(new Mapping
    {
        LocalFolderPath = string.IsNullOrWhiteSpace(LocalFolderPath) ? null : LocalFolderPath.Trim(),
        Name = Name,
        BucketName = BucketName,
    });

    /// <summary>True when a location has been chosen explicitly rather than inherited from the default.</summary>
    public bool UsesCustomFolder => !string.IsNullOrWhiteSpace(LocalFolderPath);

    /// <summary>True when saving would move an existing mapping's folder, orphaning the old one.</summary>
    public bool FolderIsMoving =>
        _originalFolder is not null &&
        !string.Equals(_originalFolder.TrimEnd('\\'), EffectiveFolderPath.TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>The folder this mapping currently occupies, for the "moving" warning.</summary>
    public string? OriginalFolderPath => _originalFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    [NotifyPropertyChangedFor(nameof(FolderIsMoving))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    [NotifyPropertyChangedFor(nameof(FolderIsMoving))]
    private string _bucketName = string.Empty;
    [ObservableProperty] private string _subPath = string.Empty;
    [ObservableProperty] private string _driveLetter = "W";
    [ObservableProperty] private string _regionCode = "us-east-1";
    [ObservableProperty] private bool _autoMount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDriveLetterMode))]
    [NotifyPropertyChangedFor(nameof(IsOnDemandMode))]
    [NotifyPropertyChangedFor(nameof(ModeDescription))]
    private MappingMode _mode = MappingMode.OnDemandFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveFolderPath))]
    [NotifyPropertyChangedFor(nameof(UsesCustomFolder))]
    [NotifyPropertyChangedFor(nameof(FolderIsMoving))]
    private string _localFolderPath = string.Empty;

    [ObservableProperty] private VfsCacheMode _cacheMode = VfsCacheMode.Full;
    [ObservableProperty] private int _vfsCacheMaxSizeMb = 10 * 1024;
    [ObservableProperty] private double _vfsCacheMaxAgeHours = 1;
    [ObservableProperty] private double _dirCacheTimeMinutes = 5;
    [ObservableProperty] private int _bufferSizeMb = 16;
    [ObservableProperty] private string _cacheDir = string.Empty;

    [ObservableProperty] private string _accessKeyId = string.Empty;
    [ObservableProperty] private string _secretAccessKey = string.Empty;

    /// <summary>Returns an error message if the form is invalid, otherwise null.</summary>
    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(BucketName))
            return "Bucket name is required.";
        if (IsDriveLetterMode && string.IsNullOrWhiteSpace(DriveLetter))
            return "A drive letter is required.";
        if (WasabiRegion.FindByCode(RegionCode) is null)
            return "Please choose a valid region.";
        if (string.IsNullOrWhiteSpace(AccessKeyId) || string.IsNullOrWhiteSpace(SecretAccessKey))
            return "Access key and secret key are required.";
        if (IsOnDemandMode && OnDemandFolderRules.Validate(LocalFolderPath, _otherFolders) is { } folderError)
            return folderError;
        return null;
    }

    /// <summary>Resets the folder to the default location under the user profile.</summary>
    public void UseDefaultFolder() => LocalFolderPath = string.Empty;

    /// <summary>
    /// Sets the folder from a parent directory the user picked, creating a named subfolder inside it
    /// the way OneDrive does — so choosing "D:\" yields "D:\&lt;name&gt;" rather than turning the whole
    /// volume into a sync root.
    /// </summary>
    public void SetFolderFromParent(string parentDirectory)
    {
        var leaf = string.IsNullOrWhiteSpace(Name) ? BucketName : Name;
        LocalFolderPath = OnDemandFolderRules.CombineForMapping(parentDirectory, leaf);
    }

    public Mapping BuildMapping() => new()
    {
        Id = _id,
        Name = Name.Trim(),
        BucketName = BucketName.Trim(),
        SubPath = string.IsNullOrWhiteSpace(SubPath) ? null : SubPath.Trim(),
        DriveLetter = DriveLetter.TrimEnd(':'),
        RegionCode = RegionCode,
        AutoMount = AutoMount,
        Mode = Mode,
        LocalFolderPath = IsOnDemandMode && !string.IsNullOrWhiteSpace(LocalFolderPath)
            ? LocalFolderPath.Trim()
            : null,
        Cache = new CacheSettings
        {
            CacheMode = CacheMode,
            VfsCacheMaxSizeMb = VfsCacheMaxSizeMb,
            VfsCacheMaxAge = TimeSpan.FromHours(Math.Max(0, VfsCacheMaxAgeHours)),
            DirCacheTime = TimeSpan.FromMinutes(Math.Max(0, DirCacheTimeMinutes)),
            BufferSizeMb = BufferSizeMb,
            CacheDir = string.IsNullOrWhiteSpace(CacheDir) ? null : CacheDir.Trim(),
        },
    };

    public WasabiCredentials BuildCredentials() => new()
    {
        AccessKeyId = AccessKeyId.Trim(),
        SecretAccessKey = SecretAccessKey.Trim(),
    };

    private static List<string> BuildDriveLetters(string current)
    {
        var used = DriveInfo.GetDrives()
            .Select(d => d.Name.TrimEnd('\\', ':').ToUpperInvariant())
            .ToHashSet();
        var free = new List<string>();
        for (var c = 'D'; c <= 'Z'; c++)
        {
            var letter = c.ToString();
            if (!used.Contains(letter) || string.Equals(letter, current, StringComparison.OrdinalIgnoreCase))
                free.Add(letter);
        }
        return free;
    }
}
