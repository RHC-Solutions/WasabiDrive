using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WasabiDrive.Core;

/// <summary>Detects whether WinFsp (required by rclone mount) is installed.</summary>
[SupportedOSPlatform("windows")]
public static class WinFspDetector
{
    public static bool IsInstalled() => GetInstallDir() is not null;

    /// <summary>Returns WinFsp's install directory, or null if it is not installed.</summary>
    public static string? GetInstallDir()
    {
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\WinFsp");
            if (key?.GetValue("InstallDir") is string dir && Directory.Exists(dir))
                return dir;
        }
        return null;
    }
}
