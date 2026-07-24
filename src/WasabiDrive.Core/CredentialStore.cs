using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WasabiDrive.Core.Models;

namespace WasabiDrive.Core;

/// <summary>
/// Stores Wasabi secret keys encrypted at rest using Windows DPAPI (per-user scope). The
/// encrypted blob is a JSON map of mapping-id → credentials, so a secret is never written in
/// plaintext and can only be decrypted by the same Windows user account.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialStore
{
    // Extra entropy mixed into DPAPI so the blob is scoped to this app, not just the user.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WasabiDrive.v1.credentials");

    private readonly string _filePath;
    private Dictionary<string, WasabiCredentials> _cache = new();

    public CredentialStore(string? filePath = null)
    {
        _filePath = filePath ?? AppPaths.CredentialsFile;
    }

    /// <summary>Loads and decrypts the store from disk. Safe to call when the file is absent.</summary>
    public void Load()
    {
        if (!File.Exists(_filePath))
        {
            _cache = new Dictionary<string, WasabiCredentials>();
            return;
        }

        var protectedBytes = File.ReadAllBytes(_filePath);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plainBytes);
        _cache = JsonSerializer.Deserialize<Dictionary<string, WasabiCredentials>>(json)
                 ?? new Dictionary<string, WasabiCredentials>();
    }

    public WasabiCredentials? Get(Guid mappingId) =>
        _cache.TryGetValue(mappingId.ToString("N"), out var c) ? c : null;

    public void Set(Guid mappingId, WasabiCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        _cache[mappingId.ToString("N")] = credentials;
        Save();
    }

    public void Remove(Guid mappingId)
    {
        if (_cache.Remove(mappingId.ToString("N")))
            Save();
    }

    private void Save()
    {
        AppPaths.EnsureCreated();
        var json = JsonSerializer.Serialize(_cache);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);

        // Write atomically so a crash mid-write can't corrupt the store.
        var tmp = _filePath + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, _filePath, overwrite: true);
    }
}
