using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Services;

namespace Wildpinkler.App.Controls;

/// <summary>
/// Modal progress dialog shown while an archive is being extracted to disk. Open it with
/// <see cref="ShowAsync"/> before extraction and update it via <see cref="Progress"/>. Pass the install
/// operation's cancellation callback to the constructor so the Cancel button signals the worker. The
/// dialog stays open through <see cref="CloseAsync"/> so the user always sees the outcome; it also
/// closes itself when the user cancels.
/// </summary>
public sealed partial class InstallProgressDialog : ContentDialog
{
    private readonly Action _requestCancellation;
    private Task<ContentDialogResult>? _showTask;

    public InstallProgressDialog(string modName, Action requestCancellation)
    {
        InitializeComponent();
        // ContentDialog subclasses don't reliably inherit the implicit style from XAML alone.
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        _requestCancellation = requestCancellation;
        Title = $"Installing '{modName}'";
        StatusText.Text = "Reading archive…";
        Progress = new Progress<ExtractionProgress>(UpdateProgress);
    }

    /// <summary>Feed extraction updates to this dialog from the install operation's progress callback.</summary>
    public Progress<ExtractionProgress> Progress { get; }

    /// <summary>Marks the install as done while the dialog is still visible.</summary>
    public void MarkFinished(string statusText)
    {
        CancelButton.Visibility = Visibility.Collapsed;
        ProgressBar.Value = 100;
        StatusText.Text = statusText;
    }

    /// <summary>Closes the dialog, awaiting its show task if it is still open.</summary>
    public async Task CloseAsync()
    {
        var showTask = _showTask;
        _showTask = null;
        if (showTask is not null)
            await showTask;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        CancelButton.IsEnabled = false;
        StatusText.Text = "Cancelling…";
        _requestCancellation();
        // Dismiss the modal; the caller's install loop observes cancellation and shows the result.
        _ = CloseAsync();
    }

    private void UpdateProgress(ExtractionProgress update)
    {
        ProgressBar.Value = update.FilesTotal is > 0
            ? Math.Min(100, 100.0 * update.FilesDone / update.FilesTotal)
            : 0;
        StatusText.Text = update.FilesTotal is > 0
            ? $"{update.CurrentEntry} ({update.FilesDone} of {update.FilesTotal} files, {FormatBytes(update.BytesWritten)})"
            : $"Extracting {update.CurrentEntry}";
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
