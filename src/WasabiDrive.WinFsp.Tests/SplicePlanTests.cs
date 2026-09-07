using WasabiDrive.CloudFiles;
using WasabiDrive.WinFsp;

namespace WasabiDrive.WinFsp.Tests;

public class DirtyRangesTests
{
    [Fact]
    public void OverlappingWritesCoalesceIntoOneRange()
    {
        var ranges = new DirtyRanges();
        ranges.Add(100, 200);
        ranges.Add(150, 300);

        Assert.Equal(1, ranges.Count);
        Assert.Equal((100, 300), ranges.Ranges[0]);
        Assert.Equal(200, ranges.TotalBytes);
    }

    [Fact]
    public void AdjacentWritesCoalesce()
    {
        var ranges = new DirtyRanges();
        ranges.Add(0, 100);
        ranges.Add(100, 200);

        Assert.Equal(1, ranges.Count);
        Assert.Equal((0, 200), ranges.Ranges[0]);
    }

    [Fact]
    public void SequentialWriterCollapsesToASingleRange()
    {
        // The case that matters for memory: a file copied onto the drive arrives as thousands of
        // small sequential writes and must not leave thousands of tracked ranges behind.
        var ranges = new DirtyRanges();
        for (long offset = 0; offset < 4096 * 5000; offset += 4096)
            ranges.Add(offset, offset + 4096);

        Assert.Equal(1, ranges.Count);
        Assert.Equal(4096L * 5000, ranges.TotalBytes);
    }

    [Fact]
    public void DisjointWritesStaySeparateAndSorted()
    {
        var ranges = new DirtyRanges();
        ranges.Add(500, 600);
        ranges.Add(100, 200);
        ranges.Add(300, 400);

        Assert.Equal(3, ranges.Count);
        Assert.Equal((100, 200), ranges.Ranges[0]);
        Assert.Equal((300, 400), ranges.Ranges[1]);
        Assert.Equal((500, 600), ranges.Ranges[2]);
    }

    [Fact]
    public void AWriteBridgingTwoRangesMergesAllThree()
    {
        var ranges = new DirtyRanges();
        ranges.Add(0, 100);
        ranges.Add(500, 600);
        ranges.Add(50, 550);

        Assert.Equal(1, ranges.Count);
        Assert.Equal((0, 600), ranges.Ranges[0]);
    }

    [Fact]
    public void EmptyAndInvertedWritesAreIgnored()
    {
        var ranges = new DirtyRanges();
        ranges.Add(100, 100);
        ranges.Add(200, 150);

        Assert.True(ranges.IsEmpty);
    }

    [Fact]
    public void ClipToDropsAndTrimsRangesPastATruncation()
    {
        var ranges = new DirtyRanges();
        ranges.Add(0, 100);
        ranges.Add(200, 300);
        ranges.Add(400, 500);

        ranges.ClipTo(250);

        Assert.Equal(2, ranges.Count);
        Assert.Equal((0, 100), ranges.Ranges[0]);
        Assert.Equal((200, 250), ranges.Ranges[1]);
    }
}

public class SplicePlanTests
{
    private const long Mib = 1024 * 1024;
    private const long PartSize = 16 * Mib;

    [Fact]
    public void PartsTileTheWholeObjectWithoutGapsOrOverlap()
    {
        var length = 100 * Mib;
        var plan = SplicePlan.Plan(length, PartSize, new[] { (0L, 1L) });

        Assert.Equal(0, plan[0].Start);
        Assert.Equal(length, plan[^1].End);

        for (var i = 1; i < plan.Count; i++)
            Assert.Equal(plan[i - 1].End, plan[i].Start);

        Assert.Equal(length, plan.Sum(p => p.Length));
    }

    [Fact]
    public void PartNumbersAreOneBasedAndSequential()
    {
        var plan = SplicePlan.Plan(100 * Mib, PartSize, Array.Empty<(long, long)>());

        for (var i = 0; i < plan.Count; i++)
            Assert.Equal(i + 1, plan[i].PartNumber);
    }

    [Fact]
    public void OnlyThePartsHoldingChangedBytesAreUploaded()
    {
        // One 4 KiB write 50 MiB into a 1 GiB file: exactly one 16 MiB part should upload.
        var length = 1024 * Mib;
        var plan = SplicePlan.Plan(length, PartSize, new[] { (50 * Mib, 50 * Mib + 4096) });

        var dirty = plan.Where(p => p.Dirty).ToList();
        Assert.Single(dirty);
        Assert.Equal(4, dirty[0].PartNumber);      // part index 3 covers 48-64 MiB
        Assert.Equal(48 * Mib, dirty[0].Start);
        Assert.Equal(64 * Mib, dirty[0].End);
        Assert.Equal(PartSize, dirty.Sum(p => p.Length));

        // The remaining 1008 MiB is copied server-side rather than re-uploaded.
        Assert.Equal(length - PartSize, plan.Where(p => !p.Dirty).Sum(p => p.Length));
    }

    [Fact]
    public void AWriteStraddlingAPartBoundaryMarksBothParts()
    {
        var plan = SplicePlan.Plan(100 * Mib, PartSize, new[] { (PartSize - 10, PartSize + 10) });

        var dirty = plan.Where(p => p.Dirty).Select(p => p.PartNumber).ToList();
        Assert.Equal(new[] { 1, 2 }, dirty);
    }

    [Fact]
    public void AWriteEndingExactlyOnAPartBoundaryDoesNotDirtyTheNextPart()
    {
        // End is exclusive: [0, PartSize) is part 1 alone. Off-by-one here would silently
        // re-upload an extra part on every sequential write.
        var plan = SplicePlan.Plan(100 * Mib, PartSize, new[] { (0L, PartSize) });

        Assert.Equal(new[] { 1 }, plan.Where(p => p.Dirty).Select(p => p.PartNumber));
    }

    [Fact]
    public void TheFinalPartMayBeShorterThanThePartSize()
    {
        var length = PartSize + 1000;
        var plan = SplicePlan.Plan(length, PartSize, new[] { (0L, 10L) });

        Assert.Equal(2, plan.Count);
        Assert.Equal(1000, plan[1].Length);
    }

    [Fact]
    public void DirtyRangesPastTheEndOfATruncatedObjectAreIgnored()
    {
        // A file written to 100 MiB then truncated to 20 MiB: the stale dirty tail must not
        // produce parts beyond the new length.
        var plan = SplicePlan.Plan(20 * Mib, PartSize, new[] { (90 * Mib, 100 * Mib) });

        Assert.Equal(2, plan.Count);
        Assert.Equal(20 * Mib, plan[^1].End);
        Assert.DoesNotContain(plan, p => p.End > 20 * Mib);
    }

    [Fact]
    public void EveryNonFinalPartMeetsTheFiveMebibyteMinimum()
    {
        // S3 rejects a multipart upload whose non-final parts are under 5 MiB.
        var plan = SplicePlan.Plan(100 * Mib, PartSize, Array.Empty<(long, long)>());

        foreach (var part in plan.Take(plan.Count - 1))
            Assert.True(part.Length >= WasabiS3Client.MinimumPartSizeBytes);
    }

    [Fact]
    public void AZeroLengthObjectPlansNoParts()
    {
        Assert.Empty(SplicePlan.Plan(0, PartSize, Array.Empty<(long, long)>()));
    }

    [Theory]
    [InlineData(100L)]              // 100 bytes
    [InlineData(100L * 1024)]       // 100 KiB
    [InlineData(1024L * 1024)]      // 1 MiB
    public void SmallObjectsAreNotSpliced(long length)
    {
        var partSize = SplicePlan.ChoosePartSize(length);
        Assert.False(SplicePlan.ShouldSplice(length, length, 10, false, partSize));
    }

    [Fact]
    public void ANewObjectIsNeverSpliced()
    {
        // Nothing to copy parts from.
        var length = 1024 * Mib;
        Assert.False(SplicePlan.ShouldSplice(length, 0, length, true, PartSize));
    }

    [Fact]
    public void AnObjectChangedAlmostEverywhereIsPutWhole()
    {
        var length = 1024 * Mib;
        Assert.False(SplicePlan.ShouldSplice(length, length, (long)(length * 0.95), false, PartSize));
    }

    [Fact]
    public void ALargeObjectWithASmallEditIsSpliced()
    {
        var length = 1024 * Mib;
        Assert.True(SplicePlan.ShouldSplice(length, length, 4096, false, PartSize));
    }

    [Theory]
    [InlineData(1024L * 1024 * 1024)]                   // 1 GiB
    [InlineData(100L * 1024 * 1024 * 1024)]             // 100 GiB
    [InlineData(1024L * 1024 * 1024 * 1024)]            // 1 TiB
    [InlineData(5L * 1024 * 1024 * 1024 * 1024)]        // 5 TiB, the S3 object ceiling
    public void PartSizeAlwaysKeepsThePartCountUnderTenThousand(long length)
    {
        var partSize = SplicePlan.ChoosePartSize(length);
        var partCount = (length + partSize - 1) / partSize;

        Assert.True(partCount <= WasabiS3Client.MaxPartCount,
            $"{length} bytes at {partSize} bytes per part needs {partCount} parts");
        Assert.True(partSize >= WasabiS3Client.MinimumPartSizeBytes);
    }

    [Fact]
    public void PlanRejectsANonPositivePartSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SplicePlan.Plan(100, 0, Array.Empty<(long, long)>()));
    }
}
