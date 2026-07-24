using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WasabiDrive.Core;

/// <summary>
/// Registers the app to launch at user logon via the per-user
/// <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> key. This runs in the interactive
/// user session (so rclone's drive letters are natively visible in Explorer) and — unlike a
/// Scheduled Task in the root folder — needs no administrator rights, so it works for the
/// non-elevated per-user install.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutoMountManager
{
    /// <summary>Value name under the Run key. Kept stable so we overwrite our own entry.</summary>
    public const string RunValueName = "WasabiDrive";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string SilentFlag = "--silent-automount";

    private readonly string _appExePath;

    public AutoMountManager(string appExePath)
    {
        if (string.IsNullOrWhiteSpace(appExePath))
            throw new ArgumentException("App executable path is required.", nameof(appExePath));
        _appExePath = appExePath;
    }

    /// <summary>The exact command written to the Run key.</summary>
    private string RunCommand => $"\"{_appExePath}\" {SilentFlag}";

    public bool IsInstalled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string value
            && value.Contains(_appExePath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Creates/replaces the logon entry that starts the app with the silent auto-mount flag.</summary>
    public void Install()
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Could not open the per-user Run registry key.");
        key.SetValue(RunValueName, RunCommand, RegistryValueKind.String);
    }

    public void Uninstall()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(RunValueName) is not null)
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled) Install();
        else Uninstall();
    }
}
