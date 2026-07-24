namespace WasabiDrive.Core.Models;

/// <summary>
/// Wasabi S3 access credentials for a single mapping. These are stored encrypted at rest
/// (see <see cref="CredentialStore"/>) and never written to the plaintext mappings file.
/// </summary>
public sealed class WasabiCredentials
{
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(AccessKeyId) && !string.IsNullOrWhiteSpace(SecretAccessKey);
}
