using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WasabiDrive.Core;

/// <summary>
/// Publishes the rc endpoint of each live mount so another process can reach it.
///
/// This exists because the Explorer right-click verbs run as a separate, short-lived
/// <c>WasabiDrive.exe --shell …</c> process: it does the bulk work against S3 and then has to tell
/// the *running* app's mount to drop its stale directory listing. The two processes share nothing
/// else, so the endpoint goes through a file.
///
/// It is written with DPAPI for the same reason as the credential store: the record holds the rc
/// password, and that password can drive the mount (including reading back its Wasabi config).
/// Entries are runtime state, not settings — the file is rewritten as mounts come and go, and a
/// leftover entry from a crashed run is harmless: the rc call simply fails and the caller falls
/// back to letting the directory cache expire on its own.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MountRuntimeStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WasabiDrive.v1.mountruntime");

    private readonly string _filePath;

    public MountRuntimeStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.MountRuntimeFile;
    }

    /// <summary>Records (or replaces) the rc endpoint for a mounted mapping.</summary>
    public void Publish(Guid mappingId, RcEndpoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        var all = Load();
        all[mappingId.ToString("N")] = endpoint;
        Save(all);
    }

    /// <summary>Removes a mapping's entry when its mount goes away.</summary>
    public void Remove(Guid mappingId)
    {
        var all = Load();
        if (all.Remove(mappingId.ToString("N")))
            Save(all);
    }

    /// <summary>Returns the live rc endpoint for a mapping, or null if it is not mounted.</summary>
    public RcEndpoint? Get(Guid mappingId) =>
        Load().TryGetValue(mappingId.ToString("N"), out var endpoint) ? endpoint : null;

    /// <summary>Drops every entry — used at startup and shutdown, when no mount is live.</summary>
    public void Clear()
    {
        try { if (File.Exists(_filePath)) File.Delete(_filePath); }
        catch { /* best-effort: a stale entry only costs a failed rc call */ }
    }

    private Dictionary<string, RcEndpoint> Load()
    {
        try
        {
            if (!File.Exists(_filePath))
                return new Dictionary<string, RcEndpoint>();

            var protectedBytes = File.ReadAllBytes(_filePath);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return JsonSerializer.Deserialize<Dictionary<string, RcEndpoint>>(plainBytes)
                   ?? new Dictionary<string, RcEndpoint>();
        }
        catch
        {
            // Corrupt or undecryptable runtime state is not worth failing over; start clean.
            return new Dictionary<string, RcEndpoint>();
        }
    }

    private void Save(Dictionary<string, RcEndpoint> all)
    {
        try
        {
            AppPaths.EnsureCreated();
            var plainBytes = JsonSerializer.SerializeToUtf8Bytes(all);
            var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

            var tmp = _filePath + ".tmp";
            File.WriteAllBytes(tmp, protectedBytes);
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch
        {
            // Losing this only costs the immediate cache refresh, never the mount itself.
        }
    }
}
