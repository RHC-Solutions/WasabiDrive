using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.ViewModels;

/// <summary>Backing model for the Add/Edit mapping dialog.</summary>
public sealed partial class MappingEditViewModel : ObservableObject
{
    private readonly Guid _id;

    public MappingEditViewModel(Mapping? existing, WasabiCredentials? credentials, AppSettings settings)
    {
        _id = existing?.Id ?? Guid.NewGuid();
        IsNew = existing is null;

        var m = existing ?? new Mapping { Cache = settings.DefaultCache.Clone() };
        Name = m.Name;
        BucketName = m.BucketName;
        SubPath = m.SubPath ?? string.Empty;
        RegionCode = m.RegionCode;
        AutoMount = m.AutoMount;
        Mode = m.Mode;
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

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _bucketName = string.Empty;
    [ObservableProperty] private string _subPath = string.Empty;
    [ObservableProperty] private string _driveLetter = "W";
    [ObservableProperty] private string _regionCode = "us-east-1";
    [ObservableProperty] private bool _autoMount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDriveLetterMode))]
    [NotifyPropertyChangedFor(nameof(IsOnDemandMode))]
    private MappingMode _mode = MappingMode.DriveLetter;

    [ObservableProperty] private string _localFolderPath = string.Empty;

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
        return null;
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
