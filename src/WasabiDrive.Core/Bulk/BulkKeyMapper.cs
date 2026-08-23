namespace WasabiDrive.Core.Bulk;

/// <summary>
/// Pure prefix/key arithmetic for bulk moves. Kept free of the S3 SDK so the rules that decide
/// where an object lands -- and which moves are refused outright -- can be unit-tested without a
/// network or a bucket.
///
/// S3 keys are case-sensitive and have no directory entities, so a "folder" here is just a key
/// prefix ending in '/'. The 0-byte directory-marker objects that the mount writes (see
/// <see cref="RcloneConfigWriter"/>'s DIRECTORY_MARKERS) are ordinary keys under that prefix and
/// need no special handling: they list, copy and delete like anything else.
/// </summary>
public static class BulkKeyMapper
{
    /// <summary>
    /// Canonicalises a folder prefix: no leading slash, exactly one trailing slash, and the empty
    /// string for the bucket root. Null/blank means "the whole bucket". Backslashes are accepted
    /// so a path pasted from Explorer maps onto S3 key separators.
    /// </summary>
    public static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;

        var trimmed = prefix.Replace('\\', '/').Trim().Trim('/');
        return trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>
    /// Rewrites <paramref name="key"/> from under <paramref name="sourcePrefix"/> to the matching
    /// key under <paramref name="destPrefix"/>. Both prefixes must already be normalized.
    /// </summary>
    public static string MapKey(string key, string sourcePrefix, string destPrefix)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.StartsWith(sourcePrefix, StringComparison.Ordinal))
            throw new ArgumentException($"Key '{key}' is not under prefix '{sourcePrefix}'.", nameof(key));

        return destPrefix + key[sourcePrefix.Length..];
    }

    /// <summary>
    /// Rejects moves that would corrupt data: a no-op onto itself, or a folder into its own
    /// subtree (which would keep re-listing the objects it had just created). Returns the
    /// normalized pair so callers do not normalize twice.
    /// </summary>
    public static (string Source, string Destination) ValidateMove(string? sourcePrefix, string? destPrefix)
    {
        var source = NormalizePrefix(sourcePrefix);
        var destination = NormalizePrefix(destPrefix);

        if (string.Equals(source, destination, StringComparison.Ordinal))
            throw new InvalidOperationException("Source and destination are the same folder.");

        // Covers both "move a folder into itself" and "move the whole bucket into one of its folders".
        if (destination.StartsWith(source, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Cannot move '{(source.Length == 0 ? "/" : source)}' into its own subfolder '{destination}'.");

        return (source, destination);
    }
}
