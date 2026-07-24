using WasabiDrive.Core;
using WasabiDrive.Core.Models;
using Xunit;

namespace WasabiDrive.Tests;

public class RcloneConfigWriterTests
{
    private static Mapping Sample() => new()
    {
        Name = "Wasabi",
        BucketName = "my-bucket",
        DriveLetter = "W",
        RegionCode = "eu-central-1",
    };

    private static WasabiCredentials Creds() => new()
    {
        AccessKeyId = "AKID",
        SecretAccessKey = "SECRET",
    };

    [Fact]
    public void BuildRemoteEnvironment_EnablesDirectoryMarkers()
    {
        var env = RcloneConfigWriter.BuildRemoteEnvironment(Sample(), Creds());

        // The remote name is derived from the mapping id, so match by suffix instead of the full key.
        var markerKey = env.Keys.Single(k => k.EndsWith("_DIRECTORY_MARKERS", StringComparison.Ordinal));
        Assert.Equal("true", env[markerKey]);
    }

    [Fact]
    public void BuildRemoteEnvironment_SetsWasabiS3Provider()
    {
        var env = RcloneConfigWriter.BuildRemoteEnvironment(Sample(), Creds());

        Assert.Contains(env, kv => kv.Key.EndsWith("_TYPE", StringComparison.Ordinal) && kv.Value == "s3");
        Assert.Contains(env, kv => kv.Key.EndsWith("_PROVIDER", StringComparison.Ordinal) && kv.Value == "Wasabi");
        Assert.Contains(env, kv => kv.Key.EndsWith("_SECRET_ACCESS_KEY", StringComparison.Ordinal) && kv.Value == "SECRET");
    }
}
