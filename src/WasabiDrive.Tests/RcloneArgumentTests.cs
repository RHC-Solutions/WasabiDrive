using WasabiDrive.Core;
using WasabiDrive.Core.Models;
using Xunit;

namespace WasabiDrive.Tests;

public class RcloneArgumentTests
{
    private static Mapping SampleMapping() => new()
    {
        Name = "Backups",
        BucketName = "my-bucket",
        DriveLetter = "W",
        RegionCode = "eu-central-1",
        Cache = new CacheSettings
        {
            CacheMode = VfsCacheMode.Full,
            VfsCacheMaxSizeMb = 2048,
            VfsCacheMaxAge = TimeSpan.FromHours(2),
            DirCacheTime = TimeSpan.FromMinutes(5),
            BufferSizeMb = 16,
        },
    };

    [Fact]
    public void BuildMountArguments_IncludesTargetsAndCacheFlags()
    {
        var args = RcloneRunner.BuildMountArguments(SampleMapping());

        Assert.Equal("mount", args[0]);
        Assert.Contains("W:", args);
        Assert.Contains("my-bucket", string.Join(" ", args));

        var joined = string.Join(" ", args);
        Assert.Contains("--vfs-cache-mode full", joined);
        Assert.Contains("--vfs-cache-max-size 2048Mi", joined);
        Assert.Contains("--vfs-cache-max-age 7200s", joined);
        Assert.Contains("--dir-cache-time 300s", joined);
        Assert.Contains("--buffer-size 16Mi", joined);
        Assert.Contains("--volname Backups", joined);
        // Network-drive mode: no Windows Recycle Bin, so deletes are real S3 deletes.
        Assert.Contains("--network-mode", joined);
    }

    [Fact]
    public void BuildMountArguments_CacheOff_OmitsCacheSizeAndAge()
    {
        var mapping = SampleMapping();
        mapping.Cache.CacheMode = VfsCacheMode.Off;

        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(mapping));

        Assert.Contains("--vfs-cache-mode off", joined);
        Assert.DoesNotContain("--vfs-cache-max-size", joined);
        Assert.DoesNotContain("--vfs-cache-max-age", joined);
    }

    [Fact]
    public void BuildMountArguments_UnlimitedCacheSize_OmitsSizeFlag()
    {
        var mapping = SampleMapping();
        mapping.Cache.VfsCacheMaxSizeMb = 0;

        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(mapping));

        Assert.DoesNotContain("--vfs-cache-max-size", joined);
        Assert.Contains("--vfs-cache-max-age", joined);
    }

    [Fact]
    public void BuildMountArguments_CacheDir_EmitsFlagWhenSet()
    {
        var mapping = SampleMapping();
        mapping.Cache.CacheDir = @"E:\WasabiCache";

        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(mapping));

        Assert.Contains(@"--cache-dir E:\WasabiCache", joined);
    }

    [Fact]
    public void BuildMountArguments_CacheDir_OmittedWhenBlank()
    {
        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(SampleMapping()));
        Assert.DoesNotContain("--cache-dir", joined);
    }

    [Fact]
    public void RemoteTarget_AppendsSubPath()
    {
        var mapping = SampleMapping();
        mapping.SubPath = "/photos/2026/";
        Assert.EndsWith(":my-bucket/photos/2026", mapping.RemoteTarget);
    }
}
