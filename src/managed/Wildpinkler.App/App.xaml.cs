using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
        AppInstance.GetCurrent().Activated += OnActivated;
        AppServices.RemoteActivationRouter.LinkReceived += OnLinkReceived;
        AppServices.RemoteActivationRouter.UnknownSchemeReceived += OnUnknownSchemeReceived;
    }

    /// <summary>Raised for a link that arrived but cannot be downloaded, so the shell can explain it.</summary>
    public static event EventHandler<RemoteLink>? LinkRejected;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // Activation arrives on a background thread; the manager needs the UI queue before any job.
        AppServices.RemoteDownloadManager.AttachDispatcher(_window.DispatcherQueue);
        AppServices.NxmProtocolRegistrar.Register();
        _ = RouteInitialActivationAsync();
    }

    public static async Task ShutdownAsync() => await AppServices.DisposeAsync();

    private void OnActivated(object? sender, AppActivationArguments args) => _ = RouteActivationAsync(args);

    private async Task RouteInitialActivationAsync() =>
        await RouteActivationAsync(AppInstance.GetCurrent().GetActivatedEventArgs());

    private async Task RouteActivationAsync(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.Protocol && args.Data is ProtocolActivatedEventArgs protocol)
            await AppServices.RemoteActivationRouter.RouteAsync(protocol.Uri);
    }

    private void OnLinkReceived(object? sender, RemoteLink link)
    {
        Post(() =>
        {
            _window?.Activate();
            if (!link.IsDownloadable)
            {
                LinkRejected?.Invoke(this, link);
                return;
            }

            if (!AppServices.AppSettings.ConfirmRemoteDownloads)
            {
                AppServices.RemoteDownloadManager.Enqueue(link);
                return;
            }

            _ = ConfirmAndEnqueueAsync(link);
        });
    }

    private async Task ConfirmAndEnqueueAsync(RemoteLink link)
    {
        var root = (_window?.Content as FrameworkElement)?.XamlRoot;
        if (root is null)
        {
            AppServices.RemoteDownloadManager.Enqueue(link);
            return;
        }

        try
        {
            var preview = await AppServices.RemoteDownloadManager.PreviewAsync(link);
            var mapping = await AppServices.RemoteGameMapper.ResolveAsync(preview.Link.ToRef());

            var dialog = new RemoteDownloadDialog(preview, mapping) { XamlRoot = root };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (dialog.SuppressFutureConfirmations)
            {
                AppServices.AppSettings.ConfirmRemoteDownloads = false;
                AppServices.AppSettingsStore.Save(AppServices.AppSettings);
            }

            AppServices.RemoteDownloadManager.Enqueue(preview.Link);
        }
        catch (RemoteSiteException exception)
        {
            LinkRejected?.Invoke(this, RemoteLink.ForUnsupported(link.SiteId, $"{exception.Message} {exception.Remedy}"));
        }
    }

    private void OnUnknownSchemeReceived(object? sender, Uri uri) =>
        Post(() => LinkRejected?.Invoke(this, RemoteLink.ForUnsupported(uri.Scheme, $"Wildpinkler cannot handle {uri.Scheme}: links.")));

    private void Post(Action action)
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null || dispatcher.HasThreadAccess)
            action();
        else
            dispatcher.TryEnqueue(() => action());
    }
}
