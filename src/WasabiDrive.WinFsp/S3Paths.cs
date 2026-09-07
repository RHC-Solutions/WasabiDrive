namespace WasabiDrive.WinFsp;

/// <summary>
/// Translates between the paths WinFsp hands us ("\a\b.txt", "\" for the root) and S3 keys
/// ("prefix/a/b.txt"). A mapping's <c>SubPath</c> becomes an invisible key prefix, so the drive
/// root is that prefix rather than the bucket root.
/// </summary>
internal sealed class S3Paths
{
    private const char Sep = '\\';
    private const string Root = "\\";

    /// <summary>"" for a whole-bucket mapping, otherwise "sub/path/".</summary>
    public string RootPrefix { get; }

    public S3Paths(string? subPath)
    {
        var trimmed = (subPath ?? string.Empty).Replace(Sep, '/').Trim('/');
        RootPrefix = trimmed.Length == 0 ? string.Empty : trimmed + "/";
    }

    /// <summary>Normalizes a WinFsp path: backslashes, no trailing separator, "\" for the root.</summary>
    public static string Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path)) return Root;
        var p = path.Replace('/', Sep);
        if (p.Length > 1) p = p.TrimEnd(Sep);
        return p.Length == 0 ? Root : p;
    }

    public static bool IsRoot(string? path) => Normalize(path) == Root;

    /// <summary>"\a\b.txt" becomes "prefix/a/b.txt". The root maps to the bare prefix.</summary>
    public string ToKey(string? path)
    {
        var p = Normalize(path);
        if (p == Root) return RootPrefix;
        return RootPrefix + p.TrimStart(Sep).Replace(Sep, '/');
    }

    /// <summary>Same as <see cref="ToKey"/> but always ending in "/" — the listing prefix.</summary>
    public string ToDirPrefix(string? path)
    {
        var key = ToKey(path);
        return key.Length == 0 || key.EndsWith('/') ? key : key + "/";
    }

    /// <summary>"\a\b.txt" becomes "\a"; "\a" becomes "\".</summary>
    public static string Parent(string? path)
    {
        var p = Normalize(path);
        if (p == Root) return Root;
        var i = p.LastIndexOf(Sep);
        return i <= 0 ? Root : p[..i];
    }

    /// <summary>"\a\b.txt" becomes "b.txt"; the root has no leaf.</summary>
    public static string Leaf(string? path)
    {
        var p = Normalize(path);
        if (p == Root) return string.Empty;
        return p[(p.LastIndexOf(Sep) + 1)..];
    }
}
