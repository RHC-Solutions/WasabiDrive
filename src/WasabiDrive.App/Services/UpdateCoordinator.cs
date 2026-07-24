using System.Diagnostics;
using System.Windows;
using WasabiDrive.Core;

namespace WasabiDrive.App.Services;

/// <summary>
/// Runs an update check against GitHub Releases and drives the user-facing prompts: offer to
/// download+run the installer when a newer build exists. Shared by the startup check and the
/// "Check for updates" button in the About dialog.
/// </summary>
public static class UpdateCoordinator
{
    private static readonly UpdateService Service = new();

    /// <summary>
    /// Checks for updates and prompts the user. When <paramref name="userInitiated"/> is false
    /// (startup check) it stays silent unless an update is available, and swallows network errors.
    /// </summary>
    public static async Task CheckAsync(Window? owner, bool userInitiated)
    {
        UpdateCheckResult result;
        try
        {
            result = await Service.CheckAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            if (userInitiated)
                MessageBox.Show(owner!,
                    $"Could not check for updates:\n{ex.Message}",
                    "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!result.IsUpdateAvailable)
        {
            if (userInitiated)
                MessageBox.Show(owner!,
                    $"You're up to date.\n\nInstalled version: {AppInfo.CurrentVersionString}",
                    "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var notes = string.IsNullOrWhiteSpace(result.ReleaseNotes)
            ? string.Empty
            : $"\n\nWhat's new:\n{Trim(result.ReleaseNotes!, 600)}";

        var prompt =
            $"A new version of WasabiDrive is available.\n\n" +
            $"Installed: {AppInfo.CurrentVersionString}\n" +
            $"Available: {result.LatestVersion}{notes}\n\n" +
            (result.InstallerUrl is null
                ? "Open the release page to download it?"
                : "Download and install it now? The app will close to complete the update.");

        var choice = MessageBox.Show(owner!, prompt, "Update available",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (choice != MessageBoxResult.Yes)
            return;

        if (result.InstallerUrl is null)
        {
            OpenUrl(result.ReleasePageUrl ?? AppInfo.GitHubUrl);
            return;
        }

        try
        {
            await Service.DownloadAndLaunchInstallerAsync(result.InstallerUrl).ConfigureAwait(true);
            // Installer is running; exit so it can overwrite the app files.
            System.Windows.Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner!,
                $"The update could not be downloaded:\n{ex.Message}\n\nOpening the release page instead.",
                "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
            OpenUrl(result.ReleasePageUrl ?? AppInfo.GitHubUrl);
        }
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* best-effort */ }
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
