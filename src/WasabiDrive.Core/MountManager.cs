using System.Collections.Concurrent;
using WasabiDrive.Core.Models;

namespace WasabiDrive.Core;

public sealed class MountStatusChangedEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required MountState State { get; init; }
    public string? Message { get; init; }
}

public sealed class MountLogEventArgs : EventArgs
{
    public required Guid MappingId { get; init; }
    public required string Line { get; init; }
}

/// <summary>
/// Orchestrates one rclone mount per mapping: launches the process, waits for the drive letter
/// to appear, tracks live state, and (optionally) restarts a mount whose process dies unexpectedly.
/// </summary>
public sealed class MountManager : IAsyncDisposable
{
    private readonly string _rcloneExePath;
    private readonly ConcurrentDictionary<Guid, MountSession> _sessions = new();

    /// <summary>Max attempts to auto-restart a mount whose process exits while it was mounted.</summary>
    public int MaxAutoRestarts { get; init; } = 2;

    /// <summary>How long to wait for the drive letter to appear before declaring failure.</summary>
    public TimeSpan MountTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Run mounts at DEBUG log level for troubleshooting.</summary>
    public bool VerboseLogging { get; init; }

    public MountManager(string rcloneExePath)
    {
        if (string.IsNullOrWhiteSpace(rcloneExePath) || !File.Exists(rcloneExePath))
            throw new FileNotFoundException("rclone.exe not found.", rcloneExePath);
        _rcloneExePath = rcloneExePath;
    }

    public event EventHandler<MountStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<MountLogEventArgs>? LogReceived;

    public MountState GetState(Guid mappingId) =>
        _sessions.TryGetValue(mappingId, out var s) ? s.State : MountState.Unmounted;

    public bool IsMounted(Guid mappingId) => GetState(mappingId) == MountState.Mounted;

    /// <summary>
    /// The loopback rc endpoint of a live mount, or null when it is not mounted. Used to tell that
    /// mount to drop a directory listing a direct-to-S3 bulk operation has just invalidated.
    /// Re-read it after every transition to <see cref="MountState.Mounted"/>: a restarted mount
    /// gets a new port.
    /// </summary>
    public RcEndpoint? GetRemoteControl(Guid mappingId) =>
        _sessions.TryGetValue(mappingId, out var s) && s.State == MountState.Mounted
            ? s.RemoteControl
            : null;

    /// <summary>
    /// Mounts <paramref name="mapping"/> using <paramref name="credentials"/> and returns once the
    /// drive is live (state <see cref="MountState.Mounted"/>) or throws on failure.
    /// </summary>
    public async Task MountAsync(Mapping mapping, WasabiCredentials credentials, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(credentials);
        if (!credentials.IsComplete)
            throw new InvalidOperationException("Wasabi credentials are incomplete.");

        if (_sessions.TryGetValue(mapping.Id, out var existing) &&
            existing.State is MountState.Mounted or MountState.Mounting)
            return;

        if (IsDriveLetterInUse(mapping.DriveTarget))
            throw new InvalidOperationException(
                $"Drive {mapping.DriveTarget} is already in use by another volume.");

        var endpoint = RcEndpoint.Allocate();
        var session = new MountSession(mapping, credentials,
            new RcloneRunner(_rcloneExePath) { VerboseLogging = VerboseLogging, RemoteControl = endpoint })
        {
            RemoteControl = endpoint,
        };
        _sessions[mapping.Id] = session;

        await StartSessionAsync(session, ct).ConfigureAwait(false);
    }

    private async Task StartSessionAsync(MountSession session, CancellationToken ct)
    {
        var mapping = session.Mapping;
        SetState(session, MountState.Mounting);

        var runner = session.Runner;
        runner.LogLineReceived += line =>
            LogReceived?.Invoke(this, new MountLogEventArgs { MappingId = mapping.Id, Line = line });
        runner.Exited += code => OnRunnerExited(session, code);

        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, session.Credentials);
        runner.Start(mapping, env);

        // Poll for the drive to appear. If the process dies first, OnRunnerExited flips us to Error.
        var deadline = DateTime.UtcNow + MountTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (session.State == MountState.Error)
                throw new InvalidOperationException(
                    session.LastError ?? "rclone exited before the drive became available.");
            if (DriveIsReady(mapping.DriveTarget))
            {
                SetState(session, MountState.Mounted);
                return;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        // Timed out: tear down and report.
        await runner.StopAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        SetState(session, MountState.Error, $"Timed out waiting for {mapping.DriveTarget} to appear.");
        throw new TimeoutException($"Mounting {mapping.DriveTarget} timed out.");
    }

    private void OnRunnerExited(MountSession session, int exitCode)
    {
        // Expected exits (during Unmount) are handled by UnmountAsync; ignore them here.
        if (session.State is MountState.Unmounting or MountState.Unmounted)
            return;

        if (session.State == MountState.Mounted &&
            session.RestartCount < MaxAutoRestarts)
        {
            session.RestartCount++;
            SetState(session, MountState.Mounting,
                $"rclone exited (code {exitCode}); restarting (attempt {session.RestartCount}).");
            _ = RestartSessionAsync(session);
            return;
        }

        SetState(session, MountState.Error, $"rclone process exited unexpectedly (code {exitCode}).");
    }

    private async Task RestartSessionAsync(MountSession session)
    {
        try
        {
            session.Runner.Dispose();
            // A fresh rc port: the old one may have been taken while this mount was down. Callers
            // re-read the endpoint when the mount reports Mounted again.
            session.RemoteControl = RcEndpoint.Allocate();
            session.Runner = new RcloneRunner(_rcloneExePath)
            {
                VerboseLogging = VerboseLogging,
                RemoteControl = session.RemoteControl,
            };
            await StartSessionAsync(session, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SetState(session, MountState.Error, $"Restart failed: {ex.Message}");
        }
    }

    /// <summary>Unmounts a mapping's drive and forgets the session.</summary>
    public async Task UnmountAsync(Guid mappingId, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(mappingId, out var session))
            return;

        SetState(session, MountState.Unmounting);
        await session.Runner.StopAsync(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
        session.Runner.Dispose();
        _sessions.TryRemove(mappingId, out _);
        SetState(session, MountState.Unmounted);
    }

    public async Task UnmountAllAsync(CancellationToken ct = default)
    {
        foreach (var id in _sessions.Keys.ToArray())
            await UnmountAsync(id, ct).ConfigureAwait(false);
    }

    private void SetState(MountSession session, MountState state, string? message = null)
    {
        session.State = state;
        if (state == MountState.Error)
            session.LastError = message;
        StatusChanged?.Invoke(this,
            new MountStatusChangedEventArgs { MappingId = session.Mapping.Id, State = state, Message = message });
    }

    /// <summary>
    /// True once the drive letter exists in the OS drive map.
    ///
    /// This deliberately does NOT touch the volume. <c>Directory.Exists(@"X:\")</c> issues a real
    /// filesystem request, and a mount cannot answer anything about its root until that root has
    /// been fully enumerated — on a bucket whose root holds hundreds of thousands of objects with
    /// no common prefixes that takes many minutes. An I/O-based probe therefore blocks, hits
    /// <see cref="MountTimeout"/>, and tears down a mount that was healthy and merely warming up,
    /// so the next attempt restarts the enumeration from scratch and the drive never appears.
    /// GetLogicalDrives reads the drive bitmask only, which WinFsp sets as soon as it registers.
    /// </summary>
    private static bool DriveIsReady(string driveTarget)
    {
        try
        {
            var letter = driveTarget.TrimEnd('\\', ':');
            if (letter.Length == 0) return false;
            return Directory.GetLogicalDrives().Any(d =>
                d.Length > 0 && char.ToUpperInvariant(d[0]) == char.ToUpperInvariant(letter[0]));
        }
        catch { return false; }
    }

    private static bool IsDriveLetterInUse(string driveTarget) => DriveIsReady(driveTarget);

    public async ValueTask DisposeAsync() => await UnmountAllAsync().ConfigureAwait(false);

    private sealed class MountSession
    {
        public MountSession(Mapping mapping, WasabiCredentials credentials, RcloneRunner runner)
        {
            Mapping = mapping;
            Credentials = credentials;
            Runner = runner;
        }

        public Mapping Mapping { get; }
        public WasabiCredentials Credentials { get; }
        public RcloneRunner Runner { get; set; }

        /// <summary>Loopback rc endpoint this mount's rclone is listening on.</summary>
        public RcEndpoint? RemoteControl { get; set; }
        public MountState State { get; set; } = MountState.Unmounted;
        public string? LastError { get; set; }
        public int RestartCount { get; set; }
    }
}
