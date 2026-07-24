using System.Runtime.InteropServices;
using WasabiDrive.Core;
using WasabiDrive.Core.Models;
using Xunit;

namespace WasabiDrive.Tests;

public class ConfigAndCredentialTests
{
    [Fact]
    public void BuildRemoteEnvironment_ProducesWasabiS3Config()
    {
        var mapping = new Mapping { RegionCode = "us-east-1", BucketName = "b" };
        var creds = new WasabiCredentials { AccessKeyId = "AK", SecretAccessKey = "SK" };

        var env = RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds);
        var prefix = "RCLONE_CONFIG_" + mapping.RemoteName.ToUpperInvariant() + "_";

        Assert.Equal("s3", env[prefix + "TYPE"]);
        Assert.Equal("Wasabi", env[prefix + "PROVIDER"]);
        Assert.Equal("s3.us-east-1.wasabisys.com", env[prefix + "ENDPOINT"]);
        Assert.Equal("AK", env[prefix + "ACCESS_KEY_ID"]);
        Assert.Equal("SK", env[prefix + "SECRET_ACCESS_KEY"]);
    }

    [Fact]
    public void BuildRemoteEnvironment_UnknownRegion_Throws()
    {
        var mapping = new Mapping { RegionCode = "nowhere-1", BucketName = "b" };
        var creds = new WasabiCredentials { AccessKeyId = "AK", SecretAccessKey = "SK" };
        Assert.Throws<InvalidOperationException>(() =>
            RcloneConfigWriter.BuildRemoteEnvironment(mapping, creds));
    }

    [Fact]
    public void MappingStore_RoundTrips()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"wd_map_{Guid.NewGuid():N}.json");
        try
        {
            var store = new MappingStore(tmp);
            var mapping = new Mapping { Name = "X", BucketName = "b", DriveLetter = "Z", AutoMount = true };
            store.Save(new[] { mapping });

            var loaded = store.Load();
            Assert.Single(loaded);
            Assert.Equal("X", loaded[0].Name);
            Assert.Equal(mapping.Id, loaded[0].Id);
            Assert.True(loaded[0].AutoMount);
        }
        finally { File.Delete(tmp); }
    }

    [Fact]
    public void CredentialStore_EncryptsAndRoundTrips()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return; // DPAPI is Windows-only.

        var tmp = Path.Combine(Path.GetTempPath(), $"wd_cred_{Guid.NewGuid():N}.dat");
        try
        {
            var id = Guid.NewGuid();
            var store = new CredentialStore(tmp);
            store.Load();
            store.Set(id, new WasabiCredentials { AccessKeyId = "AK123", SecretAccessKey = "SUPER-SECRET" });

            // On-disk bytes must NOT contain the plaintext secret.
            var raw = File.ReadAllText(tmp);
            Assert.DoesNotContain("SUPER-SECRET", raw);

            var reloaded = new CredentialStore(tmp);
            reloaded.Load();
            var got = reloaded.Get(id);
            Assert.NotNull(got);
            Assert.Equal("AK123", got!.AccessKeyId);
            Assert.Equal("SUPER-SECRET", got.SecretAccessKey);
        }
        finally { File.Delete(tmp); }
    }
}
