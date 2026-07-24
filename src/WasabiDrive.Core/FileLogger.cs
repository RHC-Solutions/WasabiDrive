namespace WasabiDrive.Core;

/// <summary>
/// Appends activity to a per-day log file under <see cref="AppPaths.LogsDir"/>
/// (<c>wasabidrive-YYYY-MM-DD.log</c>), rolling over at midnight and pruning files older than the
/// retention window. Thread-safe; the writer keeps the file share-readable so logs can be opened
/// while the app runs.
/// </summary>
public sealed class FileLogger : IDisposable
{
    private readonly string _dir;
    private readonly int _retentionDays;
    private readonly object _gate = new();

    private string? _currentDate;
    private StreamWriter? _writer;

    public FileLogger(string? logsDir = null, int retentionDays = 30)
    {
        _dir = logsDir ?? AppPaths.LogsDir;
        _retentionDays = Math.Max(1, retentionDays);
    }

    /// <summary>Full path of the log file for today.</summary>
    public string CurrentFilePath => Path.Combine(_dir, FileNameFor(DateTime.Now));

    public void Log(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (_gate)
        {
            try
            {
                var now = DateTime.Now;
                var today = now.ToString("yyyy-MM-dd");
                if (_writer is null || _currentDate != today)
                {
                    _writer?.Dispose();
                    Directory.CreateDirectory(_dir);
                    _writer = new StreamWriter(Path.Combine(_dir, FileNameFor(now)), append: true)
                    {
                        AutoFlush = true,
                    };
                    _currentDate = today;
                    PruneOldLogs();
                }
                _writer.WriteLine($"{now:HH:mm:ss.fff}  {line}");
            }
            catch
            {
                // Logging must never take down the app; drop the line on any I/O error.
            }
        }
    }

    private static string FileNameFor(DateTime day) => $"wasabidrive-{day:yyyy-MM-dd}.log";

    private void PruneOldLogs()
    {
        try
        {
            var cutoff = DateTime.Now.Date.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_dir, "wasabidrive-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                var stamp = name.Length >= 10 ? name[^10..] : null;
                if (DateTime.TryParse(stamp, out var date) && date.Date < cutoff)
                    File.Delete(file);
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
