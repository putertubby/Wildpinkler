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
    private Task<ContentDialogResult>? _showTask;
    private InstallPhase _phase;

    public InstallProgressDialog(string modName, Action requestCancellation)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _requestCancellation = requestCancellation;
        _elapsed = Stopwatch.StartNew();
        Title = $"Installing '{modName}'";
        StatusText.Text = "Reading archive…";
        Progress = new Progress<ExtractionProgress>(UpdateProgress);
    }

    /// <summary>Feed extraction updates to this dialog from the install operation's progress callback.</summary>
    public Progress<ExtractionProgress> Progress { get; }

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
    }

    /// <summary>Closes the dialog, awaiting its show task if it is still open.</summary>
    public async Task CloseAsync()
    {
        var showTask = _showTask;
        _showTask = null;
        if (showTask is not null)
            await showTask;
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
