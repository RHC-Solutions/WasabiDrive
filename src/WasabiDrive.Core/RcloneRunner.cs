using System.Diagnostics;
using System.Text;
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
            // Start warming the directory cache as soon as the mount comes up, in the background,
            // instead of on the first click. Buckets with a large flat root (e.g. an application's
            // object store, where every object sits at the top level with no common prefixes) take
            // minutes to enumerate; without this the first Explorer click is what pays that cost and
            // the drive looks frozen.
            "--vfs-refresh",
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

        AddThroughputArguments(args, c);

        if (!string.IsNullOrWhiteSpace(c.CacheDir))
        {
            args.Add("--cache-dir");
            args.Add(c.CacheDir!.Trim());
        }

        return args;
    }

    /// <summary>
    /// Adds the copy/move throughput flags. Wasabi is a high-bandwidth, high-latency-per-request
    /// store, so raw speed comes almost entirely from doing many requests concurrently and from
    /// not spending requests on things we don't need (per-object HEADs for modtimes and hashes).
    /// </summary>
    private static void AddThroughputArguments(List<string> args, CacheSettings c)
    {
        // Reads: many concurrent range GETs per open file. rclone's own S3 guidance is a high
        // stream count with a small constant chunk size (16 × 4Mi); throughput scales roughly
        // linearly with the stream count.
        if (c.ReadChunkStreams > 0)
        {
            args.Add("--vfs-read-chunk-streams");
            args.Add(c.ReadChunkStreams.ToString());
        }
        if (c.ReadChunkSizeMb > 0)
        {
            args.Add("--vfs-read-chunk-size");
            args.Add($"{c.ReadChunkSizeMb}Mi");
        }

        // Sequential read-ahead only does anything when whole files land in the cache.
        if (c.ReadAheadMb > 0 && c.CacheMode == VfsCacheMode.Full)
        {
            args.Add("--vfs-read-ahead");
            args.Add($"{c.ReadAheadMb}Mi");
        }

        // Writes: how many cached files upload at once (dominates many-small-file copies) and how
        // many multipart chunks go out per large file.
        if (c.Transfers > 0)
        {
            args.Add("--transfers");
            args.Add(c.Transfers.ToString());
        }
        if (c.UploadConcurrency > 0)
        {
            args.Add("--s3-upload-concurrency");
            args.Add(c.UploadConcurrency.ToString());
        }
        if (c.UploadChunkSizeMb > 0)
        {
            args.Add("--s3-chunk-size");
            args.Add($"{c.UploadChunkSizeMb}Mi");
        }

        // Request-count savings: reading an object's metadata modtime costs a HEAD per file, and
        // hashing to detect changes costs another. Both are avoidable on a mount.
        if (c.UseServerModTime)
            args.Add("--use-server-modtime");
        if (c.FastFingerprint)
            args.Add("--vfs-fast-fingerprint");
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
            // rclone writes its logs as UTF-8. Without forcing UTF-8 here, .NET decodes the streams
            // with the console's default code page (e.g. Windows-1252), which turns non-ASCII names
            // (Cyrillic, Hebrew, …) into mojibake like "ÐÐ¾Ð²Ð°Ñ Ð¿Ð°Ð¿ÐºÐ°" in the log window.
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
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
