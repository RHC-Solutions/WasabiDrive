using System.ComponentModel;
using System.Windows;
using WasabiDrive.Core.Bulk;

namespace WasabiDrive.App.Views;

/// <summary>
/// Progress window for a bulk S3 operation, with a Cancel that actually works.
///
/// This is the whole point of routing folder deletes and moves around the mounted drive: Explorer
/// performs them on its own UI thread with no feedback and no way out, so the window that replaces
/// it has to show what is happening and stay responsive while it does.
/// </summary>
public partial class BulkOperationWindow : Window
{
    private readonly Func<IProgress<BulkProgress>, CancellationToken, Task<BulkResult>> _operation;
    private readonly CancellationTokenSource _cts = new();

    private bool _finished;

    private BulkOperationWindow(
        string title,
        string headline,
        Func<IProgress<BulkProgress>, CancellationToken, Task<BulkResult>> operation)
    {
        InitializeComponent();
        Title = title;
        HeadlineText.Text = headline;
        PhaseText.Text = "Starting…";
        _operation = operation;
    }

    /// <summary>The operation's outcome, or null if it threw before producing one.</summary>
    public BulkResult? Result { get; private set; }

    /// <summary>The exception that ended the operation, if it failed outright.</summary>
    public Exception? Error { get; private set; }

    /// <summary>
    /// Runs <paramref name="operation"/> behind a modal progress window and returns when it has
    /// finished, been cancelled, or failed.
    /// </summary>
    public static BulkOperationWindow Run(
        string title,
        string headline,
        Func<IProgress<BulkProgress>, CancellationToken, Task<BulkResult>> operation)
    {
        var window = new BulkOperationWindow(title, headline, operation);
        window.Loaded += async (_, _) => await window.RunAsync().ConfigureAwait(true);
        window.ShowDialog();
        return window;
    }

    private async Task RunAsync()
    {
        // Progress<T> marshals to the thread that created it — this one — so the handler can touch
        // the controls directly. S3BulkOperations already throttles how often it reports.
        var progress = new Progress<BulkProgress>(OnProgress);
        try
        {
            Result = await _operation(progress, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is a normal outcome, not a failure.
        }
        catch (Exception ex)
        {
            Error = ex;
        }
        finally
        {
            Finish();
        }
    }

    private void OnProgress(BulkProgress p)
    {
        // While the listing is still running the total is provisional, so say so rather than
        // implying a fixed denominator that keeps moving.
        var total = p.ListingComplete ? $"{p.ObjectsFound:N0}" : $"{p.ObjectsFound:N0} found so far";
        PhaseText.Text = p.Phase switch
        {
            BulkPhase.Listing => $"Listing… {p.ObjectsFound:N0} found",
            BulkPhase.Copying => $"Copying {p.ObjectsDone:N0} of {total}",
            BulkPhase.Deleting => $"Deleting {p.ObjectsDone:N0} of {total}",
            _ => "Finishing…",
        };

        // Fraction is null until the listing finishes, because a ratio against a still-growing
        // total slides backwards and reads as progress being lost.
        if (p.Fraction is { } fraction)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = fraction;
        }

        DetailText.Text = p.CurrentKey ?? string.Empty;
    }

    private void Finish()
    {
        _finished = true;
        Bar.IsIndeterminate = false;
        Bar.Value = 1;
        ActionButton.Content = "Close";
        PhaseText.Text = Describe();
        DetailText.Text = FirstFailures();
    }

    private string Describe()
    {
        if (Error is not null)
            return $"Failed: {Error.Message}";
        if (Result is null)
            return "Nothing to do.";

        var parts = new List<string>();
        if (Result.ObjectsCopied > 0) parts.Add($"{Result.ObjectsCopied:N0} copied");
        if (Result.ObjectsDeleted > 0) parts.Add($"{Result.ObjectsDeleted:N0} deleted");
        if (parts.Count == 0) parts.Add("nothing to do");

        var summary = string.Join(", ", parts);
        if (Result.Canceled) summary = "Cancelled — " + summary;
        if (Result.Failures.Count > 0) summary += $", {Result.Failures.Count:N0} failed";
        return char.ToUpperInvariant(summary[0]) + summary[1..] + ".";
    }

    private string FirstFailures()
    {
        if (Result is null || Result.Failures.Count == 0)
            return string.Empty;

        var shown = Result.Failures.Take(2).Select(f => $"{f.Key}: {f.Message}");
        var more = Result.Failures.Count > 2 ? $" (+{Result.Failures.Count - 2:N0} more)" : string.Empty;
        return string.Join("; ", shown) + more;
    }

    private void OnAction(object sender, RoutedEventArgs e)
    {
        if (_finished)
        {
            Close();
            return;
        }

        ActionButton.IsEnabled = false;
        PhaseText.Text = "Cancelling — finishing the requests already in flight…";
        _cts.Cancel();
    }

    private void OnClosing(object sender, CancelEventArgs e)
    {
        // Closing the window mid-run would leave the operation going with nothing watching it.
        if (!_finished)
        {
            e.Cancel = true;
            _cts.Cancel();
        }
    }
}
