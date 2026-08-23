using System.Diagnostics;
using System.IO;
using System.Windows;
using WasabiDrive.CloudFiles;
using WasabiDrive.Core;
using WasabiDrive.App.Views;
using WasabiDrive.Core.Bulk;
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
    public const string BulkDeleteVerb = "bulkdelete";
    public const string BulkMoveVerb = "bulkmove";

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

                case BulkDeleteVerb:
                    RunBulkDelete(mapping, key, path);
                    break;

                case BulkMoveVerb:
                    RunBulkMove(mapping, key, path);
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

    // ---- bulk folder operations --------------------------------------------------------------
    //
    // These deliberately never touch the mounted drive. Letting Explorer delete or move a large
    // folder means one unlink per file and one synchronous rename per entry through WinFsp, which
    // freezes its UI thread for as long as the round-trips take and cannot be cancelled. Doing the
    // same work against S3 turns thousands of requests into a handful, with progress and a cancel
    // button — see <see cref="S3BulkOperations"/>.

    private static void RunBulkDelete(Mapping mapping, string key, string path)
    {
        var prefix = RequireFolder(mapping, key, path);

        var confirmed = MessageBox.Show(
            $"Permanently delete this folder and everything inside it, directly on Wasabi?\n\n" +
            $"s3://{mapping.BucketName}/{prefix}\n\n" +
            "This cannot be undone — the objects are removed from the bucket, not moved to a " +
            "Recycle Bin.",
            "Delete on Wasabi", MessageBoxButton.OKCancel, MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirmed != MessageBoxResult.OK)
            return;

        var window = RunBulk(mapping,
            title: "Delete on Wasabi",
            headline: $"Deleting s3://{mapping.BucketName}/{prefix}",
            operation: (bulk, progress, ct) => bulk.DeletePrefixAsync(prefix, progress, ct));

        // The parent listing still shows the folder we just removed, so that is what has to be
        // forgotten — not the folder itself.
        if (window?.Result is not null)
            RefreshMount(mapping, GetParent(prefix.TrimEnd('/')));
    }

    private static void RunBulkMove(Mapping mapping, string key, string path)
    {
        var source = RequireFolder(mapping, key, path);

        var destination = PromptWindow.Show(
            "Move on Wasabi",
            $"Move s3://{mapping.BucketName}/{source}\n\nto which folder in this bucket?",
            "A path inside the bucket, for example  archive/2024  — leave empty for the bucket root. " +
            "Objects are copied server-side on Wasabi and the originals removed; nothing is " +
            "downloaded.",
            initialValue: source.TrimEnd('/'));
        if (destination is null)
            return;

        // ValidateMove rejects a move into the folder's own subtree, which would never terminate.
        string normalizedDestination;
        try
        {
            (_, normalizedDestination) = BulkKeyMapper.ValidateMove(source, destination);
        }
        catch (Exception ex)
        {
            Warn(ex.Message);
            return;
        }

        var window = RunBulk(mapping,
            title: "Move on Wasabi",
            headline: $"Moving s3://{mapping.BucketName}/{source}\nto s3://{mapping.BucketName}/{normalizedDestination}",
            operation: (bulk, progress, ct) =>
                bulk.MovePrefixAsync(source, normalizedDestination, progress, ct));

        if (window?.Result is not null)
            RefreshMount(mapping,
                GetParent(source.TrimEnd('/')),
                GetParent(normalizedDestination.TrimEnd('/')));
    }

    /// <summary>
    /// Shared plumbing: open the client, run the operation behind the progress window, and report
    /// any per-object failures that the operation itself recorded rather than threw.
    /// </summary>
    private static BulkOperationWindow? RunBulk(
        Mapping mapping,
        string title,
        string headline,
        Func<S3BulkOperations, IProgress<BulkProgress>, CancellationToken, Task<BulkResult>> operation)
    {
        var creds = LoadCredentials(mapping.Id)
            ?? throw new InvalidOperationException("No saved credentials for this mapping.");

        using var s3 = WasabiS3Client.ForMapping(mapping, creds);
        var bulk = new S3BulkOperations(s3);

        var window = BulkOperationWindow.Run(title, headline,
            (progress, ct) => operation(bulk, progress, ct));

        if (window.Error is not null)
            Warn($"The operation failed:\n\n{window.Error.Message}");

        return window;
    }

    /// <summary>
    /// Bulk operations are only offered for drive-letter mounts. An on-demand folder keeps its own
    /// local placeholder namespace, and changing the bucket behind its back would strand
    /// placeholders for objects that no longer exist.
    /// </summary>
    private static string RequireFolder(Mapping mapping, string key, string path)
    {
        if (mapping.Mode != MappingMode.DriveLetter)
            throw new InvalidOperationException(
                "Bulk operations are only available on drive-letter mounts, not on-demand folders.");
        if (!IsDirectory(path))
            throw new InvalidOperationException("This action works on folders.");

        return BulkKeyMapper.NormalizePrefix(key);
    }

    /// <summary>
    /// Tells the running mount to drop the cached listings the operation just invalidated, so
    /// Explorer shows the change now instead of when the directory cache expires. Best-effort:
    /// if the app is not running, or the mount has gone, the cache simply ages out as usual.
    /// </summary>
    private static void RefreshMount(Mapping mapping, params string[] bucketPrefixes)
    {
        try
        {
            if (new MountRuntimeStore().Get(mapping.Id) is not { } endpoint)
                return;

            using var rc = new RcloneRcClient(endpoint);
            foreach (var prefix in bucketPrefixes.Distinct(StringComparer.Ordinal))
            {
                if (RcloneRcClient.MountRelativePath(prefix, mapping.SubPath) is { } relative)
                    rc.ForgetAsync(relative).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // A stale listing is a cosmetic problem that fixes itself; never surface it as an error.
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
