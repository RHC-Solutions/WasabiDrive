using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace WasabiDrive.Core;

/// <summary>
/// The loopback address and credentials of one mount's rclone remote-control API.
///
/// Bulk operations run against S3 directly, behind the mount's back, so afterwards the mount is
/// still serving a directory listing that no longer matches the bucket. Explorer would keep
/// showing the old tree until the listing ages out (see <c>DirCacheTime</c>) — on the large
/// buckets that need a long dir-cache-time, that is many minutes of a visibly wrong drive. The rc
/// API is how we tell that specific mount to drop the stale listing immediately.
/// </summary>
/// <param name="Port">Loopback TCP port the mount's rc server listens on.</param>
/// <param name="User">Generated per mount; the rc API is unauthenticated without it.</param>
/// <param name="Password">Generated per mount.</param>
public sealed record RcEndpoint(int Port, string User, string Password)
{
    /// <summary>
    /// Allocates a fresh endpoint on a free loopback port with random credentials.
    ///
    /// The credentials matter: an unauthenticated rc server would let any process on the machine
    /// drive this mount — including <c>config/dump</c>, which would hand over the Wasabi secret
    /// key. Binding to 127.0.0.1 keeps it off the network; the generated password keeps it away
    /// from other local processes.
    /// </summary>
    public static RcEndpoint Allocate()
    {
        // Port 0 asks the OS for a free port; we take the number and immediately release it so
        // rclone can bind it. The window between is tiny and a collision only fails the mount.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        return new RcEndpoint(port, "wasabidrive", RandomToken());
    }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    private static string RandomToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
}

/// <summary>
/// Minimal client for the handful of rclone rc calls the app needs. Every call is best-effort:
/// the mount may have been stopped, restarted on a different port, or never have come up, and
/// none of that should turn into an error in front of the user — a stale listing corrects itself
/// once the directory cache expires.
/// </summary>
public sealed class RcloneRcClient : IDisposable
{
    private readonly HttpClient _http;

    public RcloneRcClient(RcEndpoint endpoint, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        _http = new HttpClient { BaseAddress = new Uri(endpoint.BaseUrl), Timeout = timeout ?? TimeSpan.FromSeconds(10) };
        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{endpoint.User}:{endpoint.Password}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", basic);
    }

    /// <summary>
    /// Drops the cached directory listing for <paramref name="dir"/> and everything under it, so
    /// the next Explorer refresh re-reads it from Wasabi. The path is relative to the mount's own
    /// root (the bucket plus any sub-path), not to the bucket — see
    /// <see cref="MountRelativePath"/>. An empty string forgets the whole mount.
    /// </summary>
    public Task<bool> ForgetAsync(string dir, CancellationToken ct = default) =>
        PostAsync("vfs/forget", new Dictionary<string, string> { ["dir"] = dir.Trim('/') }, ct);

    /// <summary>Re-reads a directory in the background so the next click is already warm.</summary>
    public Task<bool> RefreshAsync(string dir, bool recursive = false, CancellationToken ct = default) =>
        PostAsync("vfs/refresh", new Dictionary<string, string>
        {
            ["dir"] = dir.Trim('/'),
            ["recursive"] = recursive ? "true" : "false",
        }, ct);

    private async Task<bool> PostAsync(string method, Dictionary<string, string> body, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(method, body, ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            // Mount gone, port reused, rclone too old — all just mean "no immediate refresh".
            return false;
        }
    }

    /// <summary>
    /// Converts a bucket-absolute S3 key or prefix into the path the mount knows it by, by
    /// stripping the mapping's sub-path. Returns null when the key is outside the mount entirely.
    /// </summary>
    public static string? MountRelativePath(string bucketKey, string? mappingSubPath)
    {
        var key = (bucketKey ?? string.Empty).Trim('/');
        var prefix = (mappingSubPath ?? string.Empty).Trim('/');

        if (prefix.Length == 0)
            return key;
        if (key.Equals(prefix, StringComparison.Ordinal))
            return string.Empty;
        if (!key.StartsWith(prefix + "/", StringComparison.Ordinal))
            return null;

        return key[(prefix.Length + 1)..];
    }

    public void Dispose() => _http.Dispose();
}
