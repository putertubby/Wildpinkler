using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Modal progress dialog shown while a mod is being installed. Open it with <see cref="ShowAsync"/>
/// before the install and drive it from the install operation: feed <see cref="ExtractionProgress"/>
/// updates through <see cref="Progress"/> as the archive is hashed and unpacked, and call
/// <see cref="SetPhase"/> as the install moves into phases that have no per-entry progress (scanning
/// plugins, checking dependencies, done, cancelled). The dialog stays open until the caller closes it,
/// so the user always sees the outcome.
/// </summary>
public sealed partial class InstallProgressDialog : ContentDialog
{
    private readonly Action _requestCancellation;
    private readonly Stopwatch _elapsed;
    private DispatcherTimer? _analyzingTimer;
    private AnalysisProgressReport? _analysisReport;
    private InstallPhase _phase;

    public InstallProgressDialog(string modName, Action requestCancellation)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _requestCancellation = requestCancellation;
        _elapsed = Stopwatch.StartNew();
        _analyzingTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _analyzingTimer.Tick += (sender, _) => RenderAnalyzingStatus();
        Title = $"Installing '{modName}'";
        StatusText.Text = "Reading archive…";
        Progress = new Progress<ExtractionProgress>(UpdateProgress);
        AnalysisProgress = new Progress<AnalysisProgressReport>(ReportAnalysisProgress);
    }

    /// <summary>Feed extraction updates to this dialog from the install operation's progress callback.</summary>
    public Progress<ExtractionProgress> Progress { get; }

    /// <summary>Feed analyzing-phase progress reports to this dialog from the archive analysis.</summary>
    public Progress<AnalysisProgressReport> AnalysisProgress { get; }

    /// <summary>
    /// Moves the dialog into <paramref name="phase"/> and shows <paramref name="status"/>. Phases that
    /// have a known byte budget (driven by <see cref="Progress"/> updates) show a determinate bar; the
    /// others show an indeterminate bar with the status line.
    /// </summary>
    public void SetPhase(InstallPhase phase, string status)
    {
        _phase = phase;
        StatusText.Text = status;
        var isFinal = phase is InstallPhase.Done or InstallPhase.Cancelling;
        CancelButton.Visibility = isFinal ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = !isFinal;
        if (phase != InstallPhase.Extracting)
            ProgressBar.IsIndeterminate = true;
        if (phase == InstallPhase.Analyzing)
        {
            _analysisReport = null;
            _analyzingTimer!.Start();
        }
        else
            _analyzingTimer!.Stop();
    }

    /// <summary>
    /// Records the latest analyzing-phase report. The status line and bar are refreshed by a
    /// short timer (see <see cref="RenderAnalyzingStatus"/>), so the display keeps moving even
    /// when the inspector is silent (e.g. while large FOMOD metadata files are being read).
    /// </summary>
    public void ReportAnalysisProgress(AnalysisProgressReport report)
    {
        if (_phase is not InstallPhase.Analyzing)
            return;
        _analysisReport = report;
    }

    // Reports arrive in bursts (zipping's central directory is read in milliseconds) and
    // then pause for seconds while metadata contents are read, so rendering on every report
    // would leave the UI frozen. A 250 ms timer keeps the elapsed time ticking and picks up
    // the latest report as reports arrive. The analyzing phase owns the whole bar: the entry
    // scan maps to 0–50% and the metadata file reads map to 50–100%.
    private void RenderAnalyzingStatus()
    {
        if (_phase is not InstallPhase.Analyzing)
            return;
        var elapsed = _elapsed.Elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var report = _analysisReport;
        if (report is null)
        {
            StatusText.Text = $"Analyzing… · {elapsed}";
            return;
        }

        if (report.MetadataFilesDone is null)
        {
            // Phase 1: entry scan → 0–50% of the bar.
            if (report.TotalEntries is int totalEntries && totalEntries > 0)
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 50.0 * report.EntriesScanned / totalEntries;
            }
            else
            {
                ProgressBar.IsIndeterminate = true;
            }

            var total = report.TotalEntries is int
                ? $" of {report.TotalEntries.Value:N0}"
                : string.Empty;
            StatusText.Text = $"Analyzing… {report.EntriesScanned:N0}{total} entries scanned · {elapsed}";
            return;
        }

        // Phase 2: metadata file reads → 50–100% of the bar.
        if (report.MetadataFilesTotal is int metadataTotal && metadataTotal > 0)
        {
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 50.0 + 50.0 * report.MetadataFilesDone.Value / metadataTotal;
        }
        else
        {
            // No metadata files to read (or total unknown): the phase is effectively over.
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = 100;
        }

        StatusText.Text = $"Reading FOMOD metadata… {report.MetadataFilesDone:N0} of {report.MetadataFilesTotal:N0} files · {elapsed}";
    }

    /// <summary>
    /// Closes the dialog. WinUI 3 dismisses a <see cref="ContentDialog"/> through the synchronous
    /// <see cref="ContentDialog.Hide"/> (WinUI 3 has no async hide and no <c>IsOpen</c> state); safe to
    /// call more than once.
    /// </summary>
    public Task CloseAsync()
    {
        Hide();
        return Task.CompletedTask;
    }

    // Cancelling only signals the install task; the owning page closes the dialog once the
    // cancellation has been observed and partial staging files have been removed.
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        SetPhase(InstallPhase.Cancelling, "Cancelling…");
        _requestCancellation();
    }

    private void UpdateProgress(ExtractionProgress update)
    {
        if (_phase is InstallPhase.Done or InstallPhase.Cancelling)
            return;

        var elapsed = _elapsed.Elapsed.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        if (update.BytesTotal > 0)
        {
            // A declared byte budget lets the bar be determinate and honest about throughput.
            ProgressBar.IsIndeterminate = false;
            ProgressBar.Value = Math.Min(100, 100.0 * update.BytesWritten / update.BytesTotal);
            StatusText.Text = $"{FormatBytes(update.BytesWritten)} of {FormatBytes(update.BytesTotal)} · {update.FilesDone:N0} files · {elapsed}";
        }
        else
        {
            // No byte budget (hashing, or indeterminate sizes): an animated bar with a running total.
            ProgressBar.IsIndeterminate = true;
            StatusText.Text = $"{update.CurrentEntry} · {FormatBytes(update.BytesWritten)} · {elapsed}";
        }
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30)
            return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20)
            return $"{bytes / (double)(1L << 20):0.0} MB";
        if (bytes >= 1L << 10)
            return $"{bytes / 1024d:0} KB";
        return $"{bytes} B";
    }
}
