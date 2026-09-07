using System.Collections.Concurrent;
using Fsp;
using WasabiDrive.Core.Models;

namespace WasabiDrive.WinFsp;

/// <summary>
/// Mounts mappings as drive letters through <see cref="S3FileSystem"/>. Drop-in shaped like
/// <c>WasabiDrive.Core.MountManager</c>, except there is no child process: the filesystem runs
/// on WinFsp's dispatcher threads inside this process, so there is no rclone.exe to supervise,
/// no stdout to parse, and no orphaned mount to clean up after a crash.
/// </summary>
public sealed class S3MountManager : IDisposable
{
    private sealed record Session(FileSystemHost Host, S3FileSystem FileSystem, string MountPoint);

    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();
    private readonly Action<string>? _log;

    public S3MountManager(Action<string>? log = null) => _log = log;

    /// <summary>
    /// The winfsp.net build this assembly is compiled against. The managed binding refuses to
    /// load against a driver with a different major.minor, so this is also the driver requirement.
    /// Kept in lockstep with the bundled MSI — see third_party/winfsp/SOURCE.txt.
    /// </summary>
    public const string RequiredDriverVersion = "2.1";

    /// <summary>WinFsp version string, or null when the driver is unusable.</summary>
    public static string? DriverVersion => TryGetDriverVersion(out var version, out _) ? version : null;

    /// <summary>
    /// Probes the WinFsp driver. On failure <paramref name="error"/> carries a message fit to show
    /// a user, rather than the <see cref="TypeInitializationException"/> the binding raises when
    /// the installed driver is the wrong version.
    /// </summary>
    public static bool TryGetDriverVersion(out string? version, out string? error)
    {
        version = null;
        error = null;

        try
        {
            version = FileSystemHost.Version()?.ToString();
            if (version is null)
            {
                error = "WinFsp did not report a version; the driver looks broken. Reinstall WinFsp " +
                        $"{RequiredDriverVersion}.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            var root = ex;
            while (root.InnerException is not null) root = root.InnerException;

            error = root.Message.Contains("incorrect dll version", StringComparison.OrdinalIgnoreCase)
                ? $"The installed WinFsp driver is the wrong version — WasabiDrive needs " +
                  $"{RequiredDriverVersion}.x ({root.Message}). Re-run the WasabiDrive installer, " +
                  "which upgrades WinFsp when it is too old."
                : $"WinFsp could not be loaded: {root.Message}. Install WinFsp " +
                  $"{RequiredDriverVersion} and try again.";
            return false;
        }
    }

    /// <summary>Mounts a mapping on its drive letter. Throws when WinFsp rejects the mount.</summary>
    public void Mount(Mapping mapping, WasabiCredentials credentials)
    {
        if (_sessions.ContainsKey(mapping.Id))
            throw new InvalidOperationException($"'{mapping.Name}' is already mounted.");

        // Fail with something readable before WinFsp's type initializer blows up mid-mount.
        if (!TryGetDriverVersion(out _, out var driverError))
            throw new InvalidOperationException(driverError);

        var mountPoint = mapping.DriveTarget;
        var fileSystem = new S3FileSystem(mapping, credentials, _log);
        var host = new FileSystemHost(fileSystem);

        var status = host.Mount(mountPoint, null, false, 0);
        if (status < 0)
        {
            fileSystem.Dispose();
            throw new InvalidOperationException(
                $"WinFsp refused to mount '{mountPoint}' (status 0x{status:X8}). " +
                "The drive letter may be in use, or WinFsp is not installed.");
        }

        _sessions[mapping.Id] = new Session(host, fileSystem, mountPoint);
        _log?.Invoke($"Mounted {mapping.BucketName} on {mountPoint} (WinFsp, no rclone).");
    }

    public void Unmount(Guid mappingId)
    {
        if (!_sessions.TryRemove(mappingId, out var session)) return;

        try
        {
            session.Host.Unmount();
            session.Host.Dispose();
        }
        finally
        {
            session.FileSystem.Dispose();
        }

        _log?.Invoke($"Unmounted {session.MountPoint}.");
    }

    public bool IsMounted(Guid mappingId) => _sessions.ContainsKey(mappingId);

    public void Dispose()
    {
        foreach (var id in _sessions.Keys.ToArray()) Unmount(id);
    }
}
