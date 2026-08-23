using WasabiDrive.Core;
using WasabiDrive.Core.Models;
using Xunit;

namespace WasabiDrive.Tests;

public class MountRelativePathTests
{
    [Theory]
    // No sub-path: the bucket key is already what the mount calls it.
    [InlineData("photos/2024", null, "photos/2024")]
    [InlineData("photos/2024", "", "photos/2024")]
    [InlineData("", null, "")]
    // With a sub-path, the mount root is that prefix, so it has to come off.
    [InlineData("team/photos/2024", "team", "photos/2024")]
    [InlineData("team/photos/2024", "/team/", "photos/2024")]
    // The mount root itself maps to the empty path ("forget everything").
    [InlineData("team", "team", "")]
    public void MountRelativePath_StripsTheMountSubPath(string key, string? subPath, string expected) =>
        Assert.Equal(expected, RcloneRcClient.MountRelativePath(key, subPath));

    [Theory]
    [InlineData("other/photos", "team")]
    // A shared name prefix is not containment: "teamwork/" is not inside "team/".
    [InlineData("teamwork/photos", "team")]
    public void MountRelativePath_OutsideTheMount_ReturnsNull(string key, string subPath) =>
        Assert.Null(RcloneRcClient.MountRelativePath(key, subPath));
}

public class RemoteControlArgumentTests
{
    private static Mapping SampleMapping() =>
        new() { BucketName = "bucket", DriveLetter = "W", RegionCode = "us-east-1" };

    [Fact]
    public void BuildMountArguments_WithoutEndpoint_OmitsRcFlags()
    {
        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(SampleMapping()));
        Assert.DoesNotContain("--rc", joined);
    }

    [Fact]
    public void BuildMountArguments_WithEndpoint_BindsToLoopbackOnly()
    {
        var endpoint = new RcEndpoint(5572, "u", "p");
        var args = RcloneRunner.BuildMountArguments(SampleMapping(), verbose: false, remoteControl: endpoint);
        var joined = string.Join(" ", args);

        Assert.Contains("--rc", args);
        Assert.Contains("127.0.0.1:5572", args);
        // Anything other than loopback would expose mount control to the network.
        Assert.DoesNotContain("0.0.0.0", joined);
    }

    [Fact]
    public void BuildMountArguments_NeverPutsRcCredentialsOnTheCommandLine()
    {
        // A process's command line is readable by any local process, which is why the Wasabi key
        // goes through the environment too.
        var endpoint = new RcEndpoint(5572, "the-user", "the-secret");
        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(
            SampleMapping(), verbose: false, remoteControl: endpoint));

        Assert.DoesNotContain("the-secret", joined);
        Assert.DoesNotContain("--rc-pass", joined);
        Assert.DoesNotContain("--rc-user", joined);
    }

    [Fact]
    public void BuildRemoteControlEnvironment_CarriesTheCredentials()
    {
        var env = RcloneRunner.BuildRemoteControlEnvironment(new RcEndpoint(5572, "the-user", "the-secret"));

        Assert.Equal("the-user", env["RCLONE_RC_USER"]);
        Assert.Equal("the-secret", env["RCLONE_RC_PASS"]);
    }

    [Fact]
    public void Allocate_ProducesADistinctPasswordEachTime()
    {
        var first = RcEndpoint.Allocate();
        var second = RcEndpoint.Allocate();

        Assert.NotEqual(first.Password, second.Password);
        Assert.True(first.Password.Length >= 24, "the rc password must not be trivially guessable");
        Assert.InRange(first.Port, 1, 65535);
    }
}
