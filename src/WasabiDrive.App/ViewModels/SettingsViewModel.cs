using CommunityToolkit.Mvvm.ComponentModel;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.ViewModels;

/// <summary>Backing model for the Settings dialog.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public SettingsViewModel(AppSettings settings)
    {
        StartAtLogin = settings.StartAtLogin;
        MinimizeToTray = settings.MinimizeToTray;
        AutoCheckForUpdates = settings.AutoCheckForUpdates;
        RcloneExePath = settings.RcloneExePath ?? string.Empty;

        DefaultCacheMode = settings.DefaultCache.CacheMode;
        DefaultCacheMaxSizeMb = settings.DefaultCache.VfsCacheMaxSizeMb;
        DefaultCacheMaxAgeHours = settings.DefaultCache.VfsCacheMaxAge.TotalHours;
        DefaultCacheDir = settings.DefaultCache.CacheDir ?? string.Empty;
    }

    public IReadOnlyList<VfsCacheMode> CacheModes { get; } = Enum.GetValues<VfsCacheMode>();

    [ObservableProperty] private bool _startAtLogin;
    [ObservableProperty] private bool _minimizeToTray;
    [ObservableProperty] private bool _autoCheckForUpdates;
    [ObservableProperty] private string _rcloneExePath = string.Empty;
    [ObservableProperty] private VfsCacheMode _defaultCacheMode;
    [ObservableProperty] private int _defaultCacheMaxSizeMb;
    [ObservableProperty] private double _defaultCacheMaxAgeHours;
    [ObservableProperty] private string _defaultCacheDir = string.Empty;

    /// <summary>Writes the edited values back into <paramref name="settings"/>.</summary>
    public void ApplyTo(AppSettings settings)
    {
        settings.StartAtLogin = StartAtLogin;
        settings.MinimizeToTray = MinimizeToTray;
        settings.AutoCheckForUpdates = AutoCheckForUpdates;
        settings.RcloneExePath = string.IsNullOrWhiteSpace(RcloneExePath) ? null : RcloneExePath.Trim();
        settings.DefaultCache.CacheMode = DefaultCacheMode;
        settings.DefaultCache.VfsCacheMaxSizeMb = DefaultCacheMaxSizeMb;
        settings.DefaultCache.VfsCacheMaxAge = TimeSpan.FromHours(Math.Max(0, DefaultCacheMaxAgeHours));
        settings.DefaultCache.CacheDir = string.IsNullOrWhiteSpace(DefaultCacheDir) ? null : DefaultCacheDir.Trim();
    }
}
