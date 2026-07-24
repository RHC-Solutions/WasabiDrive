using System.Reflection;

namespace WasabiDrive.Core;

/// <summary>Central product identity/branding used across the app, installer and updater.</summary>
public static class AppInfo
{
    public const string ProductName = "WasabiDrive";
    public const string Publisher = "RHC Solutions";
    public const string PublisherUrl = "https://rhcsolutions.com/";
    public const string Copyright = "© RHC Solutions. https://rhcsolutions.com/";

    /// <summary>GitHub owner/repo used by the updater and for the source link.</summary>
    public const string GitHubOwner = "RHC-Solutions";
    public const string GitHubRepo = "WasabiDrive";
    public const string GitHubUrl = "https://github.com/RHC-Solutions/WasabiDrive";

    /// <summary>The running assembly's version (from the app's &lt;Version&gt;), e.g. 0.1.0.</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version
        ?? Assembly.GetExecutingAssembly().GetName().Version
        ?? new Version(0, 0, 0);

    /// <summary>Short display form, e.g. "0.1.0".</summary>
    public static string CurrentVersionString
    {
        get
        {
            var v = CurrentVersion;
            return $"{v.Major}.{v.Minor}.{v.Build}";
        }
    }
}
