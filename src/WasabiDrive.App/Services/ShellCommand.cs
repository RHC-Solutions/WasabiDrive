using System.Diagnostics;
using System.IO;
using System.Windows;
using WasabiDrive.CloudFiles;
using WasabiDrive.Core;
using WasabiDrive.Core.Models;

namespace WasabiDrive.App.Services;

/// <summary>
/// Handles the Explorer right-click verbs (invoked as <c>WasabiDrive.exe --shell &lt;verb&gt; "path"</c>):
/// maps the clicked path to its mapping and S3 key, then copies a share link / S3 path, or opens
/// the Wasabi web console. Runs standalone (no main UI) and exits.
/// </summary>
internal static class ShellCommand
{
    public const string CopyLinkVerb = "copylink";
    public const string ConsoleVerb = "console";
    public const string CopyPathVerb = "copypath";

    private static readonly TimeSpan LinkExpiry = TimeSpan.FromDays(7);

    public static void Run(string verb, string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new InvalidOperationException("No path was supplied.");

            var (mapping, key) = Resolve(path)
                ?? throw new InvalidOperationException("This item is not inside a WasabiDrive location.");

            switch (verb.ToLowerInvariant())
            {
                case CopyPathVerb:
                    SetClipboard($"s3://{mapping.BucketName}/{key}");
                    Info($"Copied S3 path:\n\ns3://{mapping.BucketName}/{key}");
                    break;

                case ConsoleVerb:
                    OpenUrl(ConsoleUrl(mapping.BucketName, key, IsDirectory(path)));
                    break;

                case CopyLinkVerb:
                    if (IsDirectory(path))
                    {
                        Warn("Share links can only be created for files, not folders.");
                        break;
                    }
                    var creds = LoadCredentials(mapping.Id)
                        ?? throw new InvalidOperationException("No saved credentials for this mapping.");
                    using (var s3 = WasabiS3Client.ForMapping(mapping, creds))
                    {
                        var url = s3.GetPresignedUrl(key, LinkExpiry);
                        SetClipboard(url);
                        Info($"A share link (valid {LinkExpiry.TotalDays:0} days) was copied to the clipboard.");
                    }
                    break;

                default:
                    throw new InvalidOperationException($"Unknown shell verb '{verb}'.");
            }
        }
        catch (Exception ex)
        {
            Warn($"WasabiDrive could not complete that action:\n\n{ex.Message}");
        }
    }

    /// <summary>Finds the mapping whose local root contains <paramref name="path"/> and returns the S3 key.</summary>
    private static (Mapping Mapping, string Key)? Resolve(string path)
    {
        var full = Path.GetFullPath(path);
        foreach (var mapping in new MappingStore().Load())
        {
            var root = RootFor(mapping);
            if (string.IsNullOrEmpty(root)) continue;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;

            var relative = full[root.Length..].Replace('\\', '/').TrimStart('/');
            var prefix = NormalizePrefix(mapping.SubPath);
            var key = prefix + relative;
            return (mapping, key);
        }
        return null;
    }

    private static string RootFor(Mapping mapping)
    {
        if (mapping.Mode == MappingMode.OnDemandFolder)
        {
            var folder = OnDemandSyncManager.ResolveFolderPath(mapping);
            return folder.TrimEnd('\\') + "\\";
        }
        // Drive letter, e.g. "W:\".
        return mapping.DriveTarget.TrimEnd('\\') + "\\";
    }

    private static WasabiCredentials? LoadCredentials(Guid mappingId)
    {
        var store = new CredentialStore();
        store.Load();
        return store.Get(mappingId);
    }

    private static string ConsoleUrl(string bucket, string key, bool isDirectory)
    {
        // Best-effort deep link into the Wasabi web console's file manager.
        var dir = isDirectory ? key.TrimEnd('/') : GetParent(key);
        var segments = dir.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var path = string.Join('/', segments);
        var url = $"https://console.wasabisys.com/#/file_manager/{Uri.EscapeDataString(bucket)}";
        if (path.Length > 0) url += "/" + path;
        return url;
    }

    private static string GetParent(string key)
    {
        var i = key.LastIndexOf('/');
        return i < 0 ? string.Empty : key[..i];
    }

    private static bool IsDirectory(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static string NormalizePrefix(string? subPath)
    {
        if (string.IsNullOrWhiteSpace(subPath)) return string.Empty;
        var p = subPath.Trim().Trim('/');
        return p.Length == 0 ? string.Empty : p + "/";
    }

    private static void SetClipboard(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { Clipboard.SetText(text); return; }
            catch { System.Threading.Thread.Sleep(120); }
        }
        throw new InvalidOperationException("The clipboard was busy; try again.");
    }

    private static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static void Info(string message) =>
        MessageBox.Show(message, "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Information);

    private static void Warn(string message) =>
        MessageBox.Show(message, "WasabiDrive", MessageBoxButton.OK, MessageBoxImage.Warning);
}
