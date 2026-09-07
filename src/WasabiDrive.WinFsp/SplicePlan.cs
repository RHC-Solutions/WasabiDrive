using WasabiDrive.CloudFiles;

namespace WasabiDrive.WinFsp;

/// <summary>One part of a spliced multipart upload.</summary>
/// <param name="PartNumber">S3 part number, 1-based.</param>
/// <param name="Start">First byte of the part, inclusive.</param>
/// <param name="End">Last byte of the part, exclusive.</param>
/// <param name="Dirty">
/// True to upload the bytes from the local backing file; false to copy them server-side out of the
/// object's previous version.
/// </param>
internal readonly record struct SplicePart(int PartNumber, long Start, long End, bool Dirty)
{
    public long Length => End - Start;
}

/// <summary>
/// Works out how to rebuild an object from its own previous version plus the ranges that changed.
///
/// The rule that makes this safe: a part must be entirely uploaded or entirely copied, so dirty
/// ranges are widened to the part grid. Widening can only ever turn a copied part into an uploaded
/// one, which is correct but costs bandwidth — never the reverse, which would ship stale bytes.
///
/// Pure and side-effect free, so the arithmetic that decides which bytes survive an edit is
/// testable without S3 or WinFsp.
/// </summary>
internal static class SplicePlan
{
    /// <summary>Above this fraction of changed bytes, one whole-object PUT beats splicing.</summary>
    public const double WholePutDirtyFraction = 0.9;

    /// <summary>
    /// Part size for an object of <paramref name="length"/> bytes, doubled as needed to stay under
    /// S3's 10,000-part ceiling.
    /// </summary>
    public static long ChoosePartSize(long length)
    {
        var size = WasabiS3Client.SplicePartSizeBytes;
        while (size < long.MaxValue / 2 && (length + size - 1) / size > WasabiS3Client.MaxPartCount)
            size *= 2;
        return Math.Max(size, WasabiS3Client.MinimumPartSizeBytes);
    }

    /// <summary>
    /// True when rebuilding the object part-by-part is worth it. A brand-new or fully overwritten
    /// object has no previous version to copy from; an object of one part or less cannot be split;
    /// and an object that changed almost everywhere is cheaper to PUT whole.
    /// </summary>
    public static bool ShouldSplice(
        long length, long remoteLength, long dirtyBytes, bool fullUploadRequired, long partSize)
    {
        if (fullUploadRequired) return false;
        if (remoteLength <= 0) return false;
        if (length <= partSize) return false;
        return (double)dirtyBytes / length < WholePutDirtyFraction;
    }

    /// <summary>
    /// Builds the part list covering [0, <paramref name="length"/>). Every part is marked dirty
    /// (upload) or clean (server-side copy).
    /// </summary>
    public static List<SplicePart> Plan(
        long length, long partSize, IReadOnlyList<(long Start, long End)> dirtyRanges)
    {
        if (length < 0) throw new ArgumentOutOfRangeException(nameof(length));
        if (partSize <= 0) throw new ArgumentOutOfRangeException(nameof(partSize));

        var dirtyParts = new HashSet<long>();
        foreach (var (start, end) in dirtyRanges)
        {
            if (end <= start) continue;

            // Clamp to the new length: a truncation can leave dirty ranges past the end.
            var from = Math.Min(start, length);
            var to = Math.Min(end, length);
            if (to <= from) continue;

            for (var part = from / partSize; part <= (to - 1) / partSize; part++)
                dirtyParts.Add(part);
        }

        var partCount = (length + partSize - 1) / partSize;
        var plan = new List<SplicePart>((int)partCount);

        for (long part = 0; part < partCount; part++)
        {
            var start = part * partSize;
            var end = Math.Min(start + partSize, length);
            plan.Add(new SplicePart((int)part + 1, start, end, dirtyParts.Contains(part)));
        }

        return plan;
    }
}
