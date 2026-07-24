using System.IO;
using WasabiDrive.Core;
using WasabiDrive.Core.Models;
using Xunit;

namespace WasabiDrive.Tests;

public class UpdateAndPortabilityTests
{
    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.1.0", 0, 1, 0)]
    [InlineData("v1.4", 1, 4, 0)]
    [InlineData("release-2.0.3", 2, 0, 3)]
    [InlineData("v3.1.0-beta.2", 3, 1, 0)]
    public void ParseVersion_HandlesCommonTagShapes(string tag, int major, int minor, int build)
    {
        var v = UpdateService.ParseVersion(tag);
        Assert.NotNull(v);
        Assert.Equal(new Version(major, minor, build), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData(null)]
    public void ParseVersion_ReturnsNullForNonVersions(string? tag)
    {
        Assert.Null(UpdateService.ParseVersion(tag));
    }

    [Fact]
    public void FileLogger_WritesDatedFileWithTimestampedLine()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"wd-logs-{Guid.NewGuid():N}");
        try
        {
            using (var logger = new FileLogger(dir))
            {
                logger.Log("hello world");
            }

            var expected = Path.Combine(dir, $"wasabidrive-{DateTime.Now:yyyy-MM-dd}.log");
            Assert.True(File.Exists(expected), $"expected log file {expected}");
            Assert.Contains("hello world", File.ReadAllText(expected));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(".json")]
    [InlineData(".xml")]
    public void SettingsBundle_RoundTrips(string ext)
    {
        var settings = new AppSettings
        {
            StartAtLogin = true,
            AutoCheckForUpdates = false,
            DefaultCache = new CacheSettings
            {
                CacheMode = VfsCacheMode.Full,
                VfsCacheMaxSizeMb = 1_048_576,
                VfsCacheMaxAge = TimeSpan.FromHours(12),
                DirCacheTime = TimeSpan.FromMinutes(3),
                CacheDir = @"D:\cache",
            },
        };
        var mappings = new[]
        {
            new Mapping { Name = "Backups", BucketName = "b1", DriveLetter = "W" },
            new Mapping { Name = "Media", BucketName = "b2", DriveLetter = "X", AutoMount = true },
        };

        var path = Path.Combine(Path.GetTempPath(), $"wd-test-{Guid.NewGuid():N}{ext}");
        try
        {
            SettingsPortability.Export(path, settings, mappings);
            var loaded = SettingsPortability.Import(path);

            Assert.True(loaded.Settings.StartAtLogin);
            Assert.False(loaded.Settings.AutoCheckForUpdates);
            Assert.Equal(1_048_576, loaded.Settings.DefaultCache.VfsCacheMaxSizeMb);
            Assert.Equal(TimeSpan.FromHours(12), loaded.Settings.DefaultCache.VfsCacheMaxAge);
            Assert.Equal(@"D:\cache", loaded.Settings.DefaultCache.CacheDir);
            Assert.Equal(2, loaded.Mappings.Count);
            Assert.Equal("Backups", loaded.Mappings[0].Name);
            Assert.True(loaded.Mappings[1].AutoMount);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
