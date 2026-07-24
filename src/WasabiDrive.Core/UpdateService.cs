using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace WasabiDrive.Core;

/// <summary>Result of an update check.</summary>
/// <param name="IsUpdateAvailable">True when the latest release is newer than the running build.</param>
/// <param name="LatestVersion">The latest version parsed from the release tag, if any.</param>
/// <param name="ReleaseTag">Raw tag name, e.g. "v0.2.0".</param>
/// <param name="ReleaseNotes">Release body/changelog, if any.</param>
/// <param name="InstallerUrl">Download URL of the setup .exe asset, if present.</param>
/// <param name="ReleasePageUrl">Human-facing release page.</param>
public sealed record UpdateCheckResult(
    bool IsUpdateAvailable,
    Version? LatestVersion,
    string? ReleaseTag,
    string? ReleaseNotes,
    string? InstallerUrl,
    string? ReleasePageUrl);

/// <summary>
/// Checks GitHub Releases for a newer WasabiDrive build and, when the user opts in, downloads the
/// setup .exe and launches it to upgrade in place. Read-only network access to the public API;
/// no token required.
/// </summary>
public sealed class UpdateService
{
    private readonly HttpClient _http;
    private readonly Version _currentVersion;

    public UpdateService(HttpClient? http = null, Version? currentVersion = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.Add(
                new ProductInfoHeaderValue(AppInfo.ProductName, AppInfo.CurrentVersionString));
        _currentVersion = currentVersion ?? AppInfo.CurrentVersion;
    }

    private static string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{AppInfo.GitHubOwner}/{AppInfo.GitHubRepo}/releases/latest";

    /// <summary>
    /// Queries the latest release. Returns a result with <c>IsUpdateAvailable == false</c> when the
    /// app is current or when the repo has no releases yet (HTTP 404).
    /// </summary>
    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(LatestReleaseApiUrl, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return new UpdateCheckResult(false, null, null, null, null, AppInfo.GitHubUrl);
        resp.EnsureSuccessStatusCode();

        var release = await resp.Content.ReadFromJsonAsync<GitHubRelease>(cancellationToken: ct)
                          .ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Empty response from GitHub.");

        var latest = ParseVersion(release.TagName);
        var installer = release.Assets?
            .FirstOrDefault(a => a.Name is not null
                && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && a.Name.Contains("setup", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets?.FirstOrDefault(a =>
                a.Name?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true);

        var isNewer = latest is not null && latest > _currentVersion;

        return new UpdateCheckResult(
            IsUpdateAvailable: isNewer,
            LatestVersion: latest,
            ReleaseTag: release.TagName,
            ReleaseNotes: release.Body,
            InstallerUrl: installer?.BrowserDownloadUrl,
            ReleasePageUrl: release.HtmlUrl ?? AppInfo.GitHubUrl);
    }

    /// <summary>
    /// Downloads the installer to a temp file and launches it, then returns so the caller can exit
    /// the app (the installer needs the current exe unlocked to overwrite it).
    /// </summary>
    public async Task<string> DownloadAndLaunchInstallerAsync(string installerUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(installerUrl))
            throw new ArgumentException("No installer URL was provided.", nameof(installerUrl));

        var fileName = Path.GetFileName(new Uri(installerUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = $"{AppInfo.ProductName}-Setup.exe";
        var target = Path.Combine(Path.GetTempPath(), fileName);

        using (var resp = await _http.GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                   .ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = File.Create(target);
            await src.CopyToAsync(dst, ct).ConfigureAwait(false);
        }

        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        return target;
    }

    /// <summary>Parses tags like "v0.2.0", "0.2", "release-1.4.3" into a <see cref="Version"/>.</summary>
    public static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var digits = new string(tag.SkipWhile(c => !char.IsDigit(c)).ToArray());
        if (digits.Length == 0) return null;
        // Keep only the leading numeric.dotted portion (drop any -beta suffix).
        var end = 0;
        while (end < digits.Length && (char.IsDigit(digits[end]) || digits[end] == '.')) end++;
        digits = digits[..end].Trim('.');
        var parts = digits.Split('.', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            >= 3 => new Version(I(parts[0]), I(parts[1]), I(parts[2])),
            2 => new Version(I(parts[0]), I(parts[1]), 0),
            1 => new Version(I(parts[0]), 0, 0),
            _ => null,
        };
        static int I(string s) => int.TryParse(s, out var n) ? n : 0;
    }

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string? TagName { get; set; }
        [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    }
}
