namespace WasabiDrive.Core.Models;

/// <summary>App-wide settings (persisted to settings.json). Contains no secrets.</summary>
public sealed class AppSettings
{
    /// <summary>Optional explicit path to rclone.exe; null = auto-resolve.</summary>
    public string? RcloneExePath { get; set; }

    /// <summary>Default cache settings applied to newly created mappings.</summary>
    public CacheSettings DefaultCache { get; set; } = CacheSettings.Default();

    /// <summary>Whether the app is registered to launch at user logon.</summary>
    public bool StartAtLogin { get; set; }

    /// <summary>Minimize to tray instead of exiting when the window is closed.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>Check GitHub for a newer release on startup and offer to update.</summary>
    public bool AutoCheckForUpdates { get; set; } = true;
}
