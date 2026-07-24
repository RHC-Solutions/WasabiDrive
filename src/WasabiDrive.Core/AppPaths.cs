namespace WasabiDrive.Core;

/// <summary>
/// Resolves the app's per-user data directory and the files stored within it. All state lives
/// under <c>%LOCALAPPDATA%\WasabiDrive</c>.
/// </summary>
public static class AppPaths
{
    public static string BaseDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WasabiDrive");

    public static string MappingsFile => Path.Combine(BaseDir, "mappings.json");
    public static string SettingsFile => Path.Combine(BaseDir, "settings.json");
    public static string CredentialsFile => Path.Combine(BaseDir, "credentials.dat");
    public static string LogsDir => Path.Combine(BaseDir, "logs");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(BaseDir);
        Directory.CreateDirectory(LogsDir);
    }

    /// <summary>
    /// Locates <c>rclone.exe</c>: an explicit override wins, then a copy deployed alongside the
    /// app (installed layout), then the <c>third_party\rclone</c> folder used during development.
    /// Returns null if none exist.
    /// </summary>
    public static string? ResolveRcloneExe(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
            return overridePath;

        var appDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(appDir, "rclone.exe"),
            Path.Combine(appDir, "rclone", "rclone.exe"),
            Path.Combine(appDir, "third_party", "rclone", "rclone.exe"),
            // Development fallback: walk up to the repo root's third_party folder.
            Path.GetFullPath(Path.Combine(appDir, "..", "..", "..", "..", "..", "third_party", "rclone", "rclone.exe")),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
