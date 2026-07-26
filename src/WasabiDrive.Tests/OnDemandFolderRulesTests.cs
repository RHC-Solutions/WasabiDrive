using WasabiDrive.Core.Models;
using Xunit;

namespace WasabiDrive.Tests;

public class OnDemandFolderRulesTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_BlankMeansDefault_IsAllowed(string? path) =>
        Assert.Null(OnDemandFolderRules.Validate(path));

    [Fact]
    public void Validate_AcceptsAnOrdinaryFolder() =>
        Assert.Null(OnDemandFolderRules.Validate(@"D:\Wasabi\Backups"));

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"C:\")]
    [InlineData(@"D:\\")]
    public void Validate_RejectsDriveRoot(string path)
    {
        // The chosen folder becomes the sync root, so a drive root would take over the whole volume.
        var error = OnDemandFolderRules.Validate(path);
        Assert.NotNull(error);
        Assert.Contains("drive root", error);
    }

    [Theory]
    [InlineData(@"Wasabi\Backups")]
    [InlineData(@"..\Backups")]
    // "D:" is drive-RELATIVE (it means "the current directory on D:"), not the root "D:\", so it is
    // rejected for being ambiguous rather than for being a root.
    [InlineData(@"D:")]
    public void Validate_RejectsPathThatIsNotFullyQualified(string path)
    {
        var error = OnDemandFolderRules.Validate(path);
        Assert.NotNull(error);
        Assert.Contains("full path", error);
    }

    [Fact]
    public void Validate_RejectsSystemFolders()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.NotNull(OnDemandFolderRules.Validate(profile));

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs))
            Assert.NotNull(OnDemandFolderRules.Validate(docs));
    }

    [Fact]
    public void Validate_AllowsSubfolderOfProfile()
    {
        // The default location itself lives under the profile, so only the profile root is barred.
        var nested = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "WasabiDrive", "Backups");
        Assert.Null(OnDemandFolderRules.Validate(nested));
    }

    [Theory]
    [InlineData(@"D:\Wasabi\Backups")]              // exactly the other folder
    [InlineData(@"D:\Wasabi\Backups\Nested")]       // inside the other folder
    [InlineData(@"D:\Wasabi")]                      // contains the other folder
    public void Validate_RejectsOverlapWithAnotherMapping(string candidate)
    {
        var others = new[] { @"D:\Wasabi\Backups" };
        var error = OnDemandFolderRules.Validate(candidate, others);
        Assert.NotNull(error);
        Assert.Contains("overlaps", error);
    }

    [Fact]
    public void Validate_AllowsSiblingOfAnotherMapping()
    {
        var others = new[] { @"D:\Wasabi\Backups" };
        Assert.Null(OnDemandFolderRules.Validate(@"D:\Wasabi\Archive", others));
    }

    [Fact]
    public void Validate_TrailingSeparatorsDoNotDefeatOverlapCheck()
    {
        var others = new[] { @"D:\Wasabi\Backups\" };
        Assert.NotNull(OnDemandFolderRules.Validate(@"D:\Wasabi\Backups", others));
    }

    [Fact]
    public void CombineForMapping_AppendsNameLikeOneDrive() =>
        Assert.Equal(@"D:\Cloud\Backups", OnDemandFolderRules.CombineForMapping(@"D:\Cloud", "Backups"));

    [Fact]
    public void CombineForMapping_StripsInvalidCharacters() =>
        Assert.Equal(@"D:\Cloud\Backups 2026", OnDemandFolderRules.CombineForMapping(@"D:\Cloud", "Backups: 2026?"));

    [Fact]
    public void CombineForMapping_FallsBackWhenNameIsUnusable() =>
        Assert.Equal(@"D:\Cloud\WasabiDrive", OnDemandFolderRules.CombineForMapping(@"D:\Cloud", "???"));

    [Fact]
    public void HasExistingContent_FalseForMissingOrEmpty()
    {
        Assert.False(OnDemandFolderRules.HasExistingContent(null));
        Assert.False(OnDemandFolderRules.HasExistingContent(@"D:\definitely-not-a-real-folder-xyz123"));

        var empty = Directory.CreateTempSubdirectory("wasabi-empty-");
        try { Assert.False(OnDemandFolderRules.HasExistingContent(empty.FullName)); }
        finally { empty.Delete(recursive: true); }
    }

    [Fact]
    public void HasExistingContent_TrueWhenFolderHasFiles()
    {
        var dir = Directory.CreateTempSubdirectory("wasabi-full-");
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "existing.txt"), "hello");
            Assert.True(OnDemandFolderRules.HasExistingContent(dir.FullName));
        }
        finally { dir.Delete(recursive: true); }
    }
}
