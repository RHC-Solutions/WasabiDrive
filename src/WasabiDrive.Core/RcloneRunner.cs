using System.Diagnostics;
using WasabiDrive.Core.Models;

namespace WasabiDrive.Core;

/// <summary>
/// Wraps a single <c>rclone mount</c> child process: builds its argument list, injects the
/// remote's config via environment variables, captures its log output (rclone logs to stderr),
/// and terminates it on unmount (WinFsp detects the exit and releases the drive letter).
/// </summary>
public sealed class RcloneRunner : IDisposable
{
    private readonly string _rcloneExePath;
    private Process? _process;

    public RcloneRunner(string rcloneExePath)
    {
        if (string.IsNullOrWhiteSpace(rcloneExePath))
            throw new ArgumentException("rclone path is required.", nameof(rcloneExePath));
        _rcloneExePath = rcloneExePath;
    }

    /// <summary>When true, the mount runs at DEBUG log level (verbose troubleshooting).</summary>
    public bool VerboseLogging { get; set; }

    /// <summary>Raised for each line rclone writes to its log (stderr/stdout).</summary>
    public event Action<string>? LogLineReceived;

    /// <summary>Raised when the rclone process exits, with its exit code.</summary>
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };

    /// <summary>
    /// Translates a mapping's cache settings into an rclone <c>mount</c> argument list.
    /// Kept static and pure so it can be unit-tested without launching a process.
    /// </summary>
    public static IReadOnlyList<string> BuildMountArguments(Mapping mapping, bool verbose = false)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        var c = mapping.Cache;
        var args = new List<string>
        {
            "mount",
            mapping.RemoteTarget,
            mapping.DriveTarget,
            "--vfs-cache-mode", c.CacheMode.ToString().ToLowerInvariant(),
            "--dir-cache-time", ToRcloneDuration(c.DirCacheTime),
            "--buffer-size", $"{Math.Max(0, c.BufferSizeMb)}Mi",
            "--volname", string.IsNullOrWhiteSpace(mapping.Name) ? mapping.BucketName : mapping.Name,
            "--no-console",
            // Present W: as a network drive. Windows never uses a Recycle Bin on network drives,
            // so deletes become real S3 deletes instead of a server-side copy into a hidden
            // "$RECYCLE.BIN/" prefix in the bucket (which left "deleted" files/folders behind and
            // kept costing storage). Also makes folder (Dir.Remove) deletes work.
            "--network-mode",
            "--log-level", verbose ? "DEBUG" : "INFO",
        };

        if (c.CacheMode != VfsCacheMode.Off)
        {
            if (c.VfsCacheMaxSizeMb > 0)
            {
                args.Add("--vfs-cache-max-size");
                args.Add($"{c.VfsCacheMaxSizeMb}Mi");
            }
            args.Add("--vfs-cache-max-age");
            args.Add(ToRcloneDuration(c.VfsCacheMaxAge));
        }

        if (!string.IsNullOrWhiteSpace(c.CacheDir))
        {
            args.Add("--cache-dir");
            args.Add(c.CacheDir!.Trim());
        }

        return args;
    }

    /// <summary>rclone accepts durations like "3600s"; use whole seconds for an unambiguous value.</summary>
    private static string ToRcloneDuration(TimeSpan span) =>
        $"{Math.Max(0, (long)span.TotalSeconds)}s";

    /// <summary>
    /// Launches the mount. <paramref name="remoteEnv"/> comes from
    /// <see cref="RcloneConfigWriter.BuildRemoteEnvironment"/> and carries the (secret) remote config.
    /// </summary>
    public void Start(Mapping mapping, IReadOnlyDictionary<string, string> remoteEnv)
    {
        if (IsRunning)
            throw new InvalidOperationException("rclone process is already running for this mapping.");

        var psi = new ProcessStartInfo
        {
            FileName = _rcloneExePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in BuildMountArguments(mapping, VerboseLogging))
            psi.ArgumentList.Add(arg);
        foreach (var kv in remoteEnv)
            psi.Environment[kv.Key] = kv.Value;

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data != null) LogLineReceived?.Invoke(e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data != null) LogLineReceived?.Invoke(e.Data); };
        _process.Exited += (_, _) =>
        {
            var code = TryGetExitCode();
            Exited?.Invoke(code);
        };

        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    private int TryGetExitCode()
    {
        try { return _process?.ExitCode ?? -1; }
        catch { return -1; }
    }

    /// <summary>
    /// Stops the mount by terminating the rclone process; WinFsp then releases the drive letter.
    /// Returns when the process has exited (or the timeout elapses).
    /// </summary>
    public async Task StopAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        var proc = _process;
        if (proc is null || proc.HasExited)
            return;

        try
        {
            proc.Kill(entireProcessTree: true);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { /* best-effort: timed out waiting */ }
        catch (InvalidOperationException) { /* already exited */ }
    }

    public void Dispose()
    {
        try { _process?.Dispose(); }
        catch { /* ignore */ }
        _process = null;
    }
}
