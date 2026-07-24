using WasabiDrive.Core.Models;

namespace WasabiDrive.Core;

/// <summary>
/// Builds the rclone remote definition for a mapping.
///
/// Rather than writing secrets into an rclone.conf file on disk, the remote is defined
/// entirely through <c>RCLONE_CONFIG_&lt;REMOTE&gt;_&lt;KEY&gt;</c> environment variables that are
/// injected into the rclone child process at launch. This keeps the secret access key off
/// disk and off the command line (where it would be visible in the process list).
/// See https://rclone.org/docs/#config-file for the env-var override mechanism.
/// </summary>
public static class RcloneConfigWriter
{
    /// <summary>
    /// Produces the environment variables that define <paramref name="mapping"/>'s remote as a
    /// Wasabi S3 remote. Keys follow rclone's <c>RCLONE_CONFIG_{REMOTE}_{PARAM}</c> convention.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildRemoteEnvironment(
        Mapping mapping, WasabiCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(mapping);
        ArgumentNullException.ThrowIfNull(credentials);

        var region = WasabiRegion.FindByCode(mapping.RegionCode)
            ?? throw new InvalidOperationException($"Unknown Wasabi region '{mapping.RegionCode}'.");

        // Env-var remote name = uppercased remote name (rclone uppercases the whole key).
        var prefix = "RCLONE_CONFIG_" + mapping.RemoteName.ToUpperInvariant() + "_";

        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [prefix + "TYPE"] = "s3",
            [prefix + "PROVIDER"] = "Wasabi",
            [prefix + "ENV_AUTH"] = "false",
            [prefix + "REGION"] = region.RegionCode,
            [prefix + "ENDPOINT"] = region.Endpoint,
            [prefix + "ACCESS_KEY_ID"] = credentials.AccessKeyId,
            [prefix + "SECRET_ACCESS_KEY"] = credentials.SecretAccessKey,
            // S3 has no real directories. Without this, an empty folder created on the drive lives
            // only in rclone's memory (no bucket object) — so it's invisible to the Wasabi console
            // and other tools, and vanishes on remount; and deleting an empty folder can leave a
            // stale marker behind. With directory markers, rclone writes/removes a 0-byte "folder/"
            // object for empty directories, keeping the drive and the bucket consistent.
            [prefix + "DIRECTORY_MARKERS"] = "true",
        };
    }
}
