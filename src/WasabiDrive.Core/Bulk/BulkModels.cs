namespace WasabiDrive.Core.Bulk;

/// <summary>Which stage of a bulk operation is currently running.</summary>
public enum BulkPhase
{
    /// <summary>Enumerating objects under the prefix (total is not yet known).</summary>
    Listing,

    /// <summary>Server-side copying objects to their new keys (move only).</summary>
    Copying,

    /// <summary>Issuing batched DeleteObjects requests.</summary>
    Deleting,

    Completed,
}

/// <summary>
/// A progress snapshot for the UI. Listing runs concurrently with the work, so
/// <see cref="ObjectsFound"/> keeps climbing while <see cref="ObjectsDone"/> follows it;
/// treat the operation as finished when <see cref="Phase"/> is <see cref="BulkPhase.Completed"/>,
/// not when the two counts meet.
/// </summary>
public sealed record BulkProgress(
    BulkPhase Phase,
    long ObjectsFound,
    long ObjectsDone,
    long BytesFound,
    long BytesDone,
    string? CurrentKey)
{
    /// <summary>Completion ratio against what has been listed so far, or null before anything is found.</summary>
    public double? Fraction => ObjectsFound <= 0 ? null : Math.Min(1.0, (double)ObjectsDone / ObjectsFound);
}

/// <summary>One object that could not be copied or deleted. The rest of the batch still ran.</summary>
public sealed record BulkFailure(string Key, string Message);

/// <summary>Outcome of a bulk operation, including per-object failures.</summary>
public sealed record BulkResult(
    long ObjectsCopied,
    long ObjectsDeleted,
    long BytesCopied,
    IReadOnlyList<BulkFailure> Failures,
    bool Canceled)
{
    public bool Succeeded => Failures.Count == 0 && !Canceled;
}

/// <summary>
/// Tuning for a bulk operation. The defaults are chosen to make Explorer-scale operations finish
/// in seconds rather than hours: deletes go out 1000 keys per request (the S3 maximum) and copies
/// are fanned out wide, because each one is a server-side operation that costs us only a
/// round-trip — no object bytes cross this machine's connection.
/// </summary>
public sealed class BulkOptions
{
    /// <summary>Concurrent server-side CopyObject calls in flight.</summary>
    public int CopyConcurrency { get; init; } = 32;

    /// <summary>Keys per DeleteObjects request. S3 caps this at 1000.</summary>
    public int DeleteBatchSize { get; init; } = 1000;

    /// <summary>Concurrent DeleteObjects requests in flight (each carrying up to a full batch).</summary>
    public int DeleteConcurrency { get; init; } = 4;

    /// <summary>Attempts per S3 call before an object is recorded as failed.</summary>
    public int RetryAttempts { get; init; } = 4;

    /// <summary>Minimum gap between progress callbacks, so a 100k-object run can't flood the UI thread.</summary>
    public TimeSpan ProgressInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public static BulkOptions Default { get; } = new();

    public void Validate()
    {
        if (CopyConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(CopyConcurrency));
        if (DeleteBatchSize is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(DeleteBatchSize),
            "S3 DeleteObjects accepts between 1 and 1000 keys per request.");
        if (DeleteConcurrency < 1) throw new ArgumentOutOfRangeException(nameof(DeleteConcurrency));
        if (RetryAttempts < 1) throw new ArgumentOutOfRangeException(nameof(RetryAttempts));
    }
}
