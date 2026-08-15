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
        // Warm the directory cache in the background at mount, so the first click doesn't pay for
        // enumerating a bucket with a very large flat root.
        Assert.Contains("--vfs-refresh", joined);
    }

    [Fact]
    public void BuildMountArguments_IncludesThroughputFlags()
    {
        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(SampleMapping()));

        // Reads: parallel range GETs, rclone's recommended shape for S3.
        Assert.Contains("--vfs-read-chunk-streams 16", joined);
        Assert.Contains("--vfs-read-chunk-size 4Mi", joined);
        Assert.Contains("--vfs-read-ahead 128Mi", joined);
        // Writes: parallel files and parallel chunks per file.
        Assert.Contains("--transfers 8", joined);
        Assert.Contains("--s3-upload-concurrency 4", joined);
        Assert.Contains("--s3-chunk-size 16Mi", joined);
        // Request-count savings.
        Assert.Contains("--use-server-modtime", joined);
        Assert.Contains("--vfs-fast-fingerprint", joined);
    }

    [Fact]
    public void BuildMountArguments_ZeroThroughputValues_OmitFlags()
    {
        var mapping = SampleMapping();
        mapping.Cache.ReadChunkStreams = 0;
        mapping.Cache.ReadChunkSizeMb = 0;
        mapping.Cache.ReadAheadMb = 0;
        mapping.Cache.Transfers = 0;
        mapping.Cache.UploadConcurrency = 0;
        mapping.Cache.UploadChunkSizeMb = 0;
        mapping.Cache.UseServerModTime = false;
        mapping.Cache.FastFingerprint = false;

        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(mapping));

        Assert.DoesNotContain("--vfs-read-chunk-streams", joined);
        Assert.DoesNotContain("--vfs-read-chunk-size", joined);
        Assert.DoesNotContain("--vfs-read-ahead", joined);
        Assert.DoesNotContain("--transfers", joined);
        Assert.DoesNotContain("--s3-upload-concurrency", joined);
        Assert.DoesNotContain("--s3-chunk-size", joined);
        Assert.DoesNotContain("--use-server-modtime", joined);
        Assert.DoesNotContain("--vfs-fast-fingerprint", joined);
    }

    [Fact]
    public void BuildMountArguments_ReadAhead_RequiresFullCacheMode()
    {
        var mapping = SampleMapping();
        mapping.Cache.CacheMode = VfsCacheMode.Writes;

        var joined = string.Join(" ", RcloneRunner.BuildMountArguments(mapping));

        // --vfs-read-ahead only has an effect with cache-mode full.
        Assert.DoesNotContain("--vfs-read-ahead", joined);
        Assert.Contains("--vfs-read-chunk-streams", joined);
    }

    [Fact]
    public void Clone_CopiesThroughputSettings()
    {
        var original = new CacheSettings
        {
            ReadAheadMb = 1, ReadChunkStreams = 2, ReadChunkSizeMb = 3,
            Transfers = 4, UploadConcurrency = 5, UploadChunkSizeMb = 6,
            UseServerModTime = false, FastFingerprint = false,
        };

        var copy = original.Clone();

        Assert.Equal(1, copy.ReadAheadMb);
        Assert.Equal(2, copy.ReadChunkStreams);
        Assert.Equal(3, copy.ReadChunkSizeMb);
        Assert.Equal(4, copy.Transfers);
        Assert.Equal(5, copy.UploadConcurrency);
        Assert.Equal(6, copy.UploadChunkSizeMb);
        Assert.False(copy.UseServerModTime);
        Assert.False(copy.FastFingerprint);
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
