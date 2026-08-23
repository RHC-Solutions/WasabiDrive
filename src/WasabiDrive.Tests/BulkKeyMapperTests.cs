using WasabiDrive.Core.Bulk;
using Xunit;

namespace WasabiDrive.Tests;

public class BulkKeyMapperTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    [InlineData("/", "")]
    [InlineData("photos", "photos/")]
    [InlineData("/photos/", "photos/")]
    [InlineData("photos//", "photos/")]
    [InlineData("a/b/c", "a/b/c/")]
    [InlineData("a\\b", "a/b/")]
    public void NormalizePrefix_Canonicalises(string? input, string expected) =>
        Assert.Equal(expected, BulkKeyMapper.NormalizePrefix(input));

    [Fact]
    public void MapKey_RebasesOntoDestination()
    {
        Assert.Equal("archive/2024/a.jpg",
            BulkKeyMapper.MapKey("photos/2024/a.jpg", "photos/", "archive/"));
    }

    [Fact]
    public void MapKey_FromBucketRoot()
    {
        Assert.Equal("archive/a.jpg", BulkKeyMapper.MapKey("a.jpg", "", "archive/"));
    }

    [Fact]
    public void MapKey_PreservesDirectoryMarkers()
    {
        // The 0-byte marker for an empty folder must land as the marker for the new folder.
        Assert.Equal("archive/empty/", BulkKeyMapper.MapKey("photos/empty/", "photos/", "archive/"));
    }

    [Fact]
    public void MapKey_IsCaseSensitive()
    {
        // S3 keys are case-sensitive; a case-mismatched prefix is a bug, not a match.
        Assert.Throws<ArgumentException>(() => BulkKeyMapper.MapKey("Photos/a.jpg", "photos/", "archive/"));
    }

    [Fact]
    public void MapKey_KeyOutsidePrefix_Throws()
    {
        Assert.Throws<ArgumentException>(() => BulkKeyMapper.MapKey("other/a.jpg", "photos/", "archive/"));
    }

    [Fact]
    public void ValidateMove_ReturnsNormalizedPair()
    {
        var (source, destination) = BulkKeyMapper.ValidateMove("/photos", "archive/");
        Assert.Equal("photos/", source);
        Assert.Equal("archive/", destination);
    }

    [Fact]
    public void ValidateMove_SameFolder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => BulkKeyMapper.ValidateMove("photos", "/photos/"));
    }

    [Fact]
    public void ValidateMove_IntoOwnSubfolder_Throws()
    {
        // Would re-list the objects it had just created, forever.
        Assert.Throws<InvalidOperationException>(() => BulkKeyMapper.ValidateMove("photos", "photos/2024"));
    }

    [Fact]
    public void ValidateMove_WholeBucketIntoSubfolder_Throws()
    {
        Assert.Throws<InvalidOperationException>(() => BulkKeyMapper.ValidateMove(null, "archive"));
    }

    [Fact]
    public void ValidateMove_SiblingWithSharedNamePrefix_IsAllowed()
    {
        // "photos2/" starts with "photos" as a string but is not under "photos/".
        var (source, destination) = BulkKeyMapper.ValidateMove("photos", "photos2");
        Assert.Equal("photos/", source);
        Assert.Equal("photos2/", destination);
    }

    [Fact]
    public void ValidateMove_SubfolderOutToRoot_IsAllowed()
    {
        var (source, destination) = BulkKeyMapper.ValidateMove("photos/2024", "");
        Assert.Equal("photos/2024/", source);
        Assert.Equal("", destination);
    }
}

public class BulkOptionsTests
{
    [Fact]
    public void Default_IsValid() => BulkOptions.Default.Validate();

    [Fact]
    public void DeleteBatchSize_AboveS3Limit_Throws()
    {
        var options = new BulkOptions { DeleteBatchSize = 1001 };
        Assert.Throws<ArgumentOutOfRangeException>(() => options.Validate());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveConcurrency_Throws(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BulkOptions { CopyConcurrency = value }.Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new BulkOptions { DeleteConcurrency = value }.Validate());
    }
}
