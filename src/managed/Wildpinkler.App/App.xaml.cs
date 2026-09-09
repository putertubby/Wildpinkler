using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Activation;
using Wildpinkler.App.Commands;
using Wildpinkler.App.Controls;
using Wildpinkler.App.Services;
using Wildpinkler.Remote;

namespace Wildpinkler.App;

public partial class App : Application
{
    private readonly TaskCompletionSource _windowReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Window? _window;

    public App()
    {
        AppDiagnostics.Initialize();
        AppHost.Initialize(services => services.AddSingleton<IAppCommandConfirmation>(
            _ => new DialogCommandConfirmation(() => (_window?.Content as FrameworkElement)?.XamlRoot, _window?.DispatcherQueue)));
        AppDiagnostics.Verbosity.Level = AppServices.AppSettings.LogLevel;
        InitializeComponent();
        InstallGlobalExceptionHandlers();
        AppInstance.GetCurrent().Activated += OnActivated;
        AppServices.RemoteActivationRouter.LinkReceived += OnLinkReceived;
        AppServices.RemoteActivationRouter.UnknownSchemeReceived += OnUnknownSchemeReceived;
    }

    /// <summary>
    /// Every unobserved failure has to reach the log, otherwise a crash leaves nothing to report.
    /// XAML exceptions are marked handled so the shell can stay up and explain itself.
    /// </summary>
    private void InstallGlobalExceptionHandlers()
    {
        UnhandledException += (_, args) =>
        {
            AppDiagnostics.Write("Unhandled XAML exception.", args.Exception);
            args.Handled = true;
            Post(() => ReportFatal(args.Exception));
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppDiagnostics.Write("Unobserved task exception.", args.Exception);
            args.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            AppDiagnostics.Write("Unhandled AppDomain exception.", args.ExceptionObject as Exception);
            AppDiagnostics.Shutdown();
        };
    }

    private void ReportFatal(Exception exception)
    {
        var root = (_window?.Content as FrameworkElement)?.XamlRoot;
        if (root is null)
            return;

        _ = new ContentDialog
        {
            XamlRoot = root,
            Title = "Something went wrong",
            Content = $"{exception.Message}\n\nThe full details were written to the log folder.",
            PrimaryButtonText = "Open log folder",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close
        }.ShowAsync();
    }

    /// <summary>Raised for a link that arrived but cannot be downloaded, so the shell can explain it.</summary>
    public static event EventHandler<RemoteLink>? LinkRejected;

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // Activation arrives on a background thread; the manager needs the UI queue before any job.
        AppServices.DownloadQueueCoordinator.AttachDispatcher(_window.DispatcherQueue);
        _windowReady.SetResult();
        try
        {
            AppServices.NxmProtocolRegistrar.Register();
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("NXM protocol registration failed.", exception);
            RejectLink(RemoteLink.ForUnsupported("nxm", "Wildpinkler could not register itself to handle nxm: links. Check the diagnostics log for details."));
        }
        _ = RouteInitialActivationAsync();
    }

    public static async Task ShutdownAsync()
    {
        await AppServices.DisposeAsync();
        AppDiagnostics.Shutdown();
    }

    private void OnActivated(object? sender, AppActivationArguments args) => _ = RouteActivationSafelyAsync(args);

    private async Task RouteInitialActivationAsync()
    {
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        await RouteActivationSafelyAsync(args, Program.TakeInitialActivationUri());
    }

    private async Task RouteActivationSafelyAsync(AppActivationArguments args, Uri? fallbackUri = null)
    {
        try
        {
            await _windowReady.Task;
            var uri = ResolveActivationUri(args) ?? fallbackUri;
            if (uri is not null)
                await AppServices.RemoteActivationRouter.RouteAsync(uri);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("NXM activation routing failed.", exception);
            Post(() => RejectLink(RemoteLink.ForUnsupported("nxm", "Wildpinkler could not process that download link. Check the diagnostics log for details.")));
        }
    }

    private static Uri? ResolveActivationUri(AppActivationArguments args) => args.Kind switch
    {
        ExtendedActivationKind.Protocol when args.Data is ProtocolActivatedEventArgs protocol =>
            NxmActivationResolver.FromProtocolUri(protocol.Uri),
        ExtendedActivationKind.Launch when args.Data is ILaunchActivatedEventArgs launch =>
            NxmActivationResolver.FromLaunchArguments(launch.Arguments),
        _ => null,
    };

    private void OnLinkReceived(object? sender, RemoteLink link)
    {
        Post(() =>
        {
            _window?.Activate();
            if (!link.IsDownloadable)
            {
                RejectLink(link);
                return;
            }

            if (!AppServices.AppSettings.ConfirmRemoteDownloads)
            {
                AppServices.DownloadQueueCoordinator.Enqueue(link);
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
            AppDiagnostics.Write("The download confirmation dialog had no XAML root.");
            RejectLink(RemoteLink.ForUnsupported(link.SiteId, "Wildpinkler could not display the download confirmation. Try the link again."));
            return;
        }

        try
        {
            var preview = await AppServices.DownloadQueueCoordinator.PreviewAsync(link);
            var mapping = await AppServices.RemoteGameMapper.ResolveAsync(preview.Link.ToRef());

            var dialog = new RemoteDownloadDialog(preview, mapping) { XamlRoot = root };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
                return;

            if (dialog.SuppressFutureConfirmations)
            {
                AppServices.AppSettings.ConfirmRemoteDownloads = false;
                AppServices.AppSettingsStore.Save(AppServices.AppSettings);
            }

            AppServices.DownloadQueueCoordinator.Enqueue(preview.Link);
        }
        catch (RemoteSiteException exception)
        {
            RejectLink(RemoteLink.ForUnsupported(link.SiteId, $"{exception.Message} {exception.Remedy}"));
        }
        catch (OperationCanceledException exception)
        {
            AppDiagnostics.Write("NXM download preview was cancelled.", exception);
            RejectLink(RemoteLink.ForUnsupported(link.SiteId, "The download preview was cancelled. Try the link again."));
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write("NXM download preview failed.", exception);
            RejectLink(RemoteLink.ForUnsupported(link.SiteId, "Wildpinkler could not prepare that download. Check the diagnostics log for details."));
        }
    }

    private void OnUnknownSchemeReceived(object? sender, Uri uri) =>
        Post(() => RejectLink(RemoteLink.ForUnsupported(uri.Scheme, $"Wildpinkler cannot handle {uri.Scheme}: links.")));

    private void RejectLink(RemoteLink link)
    {
        _window?.Activate();
        MainWindow.Instance?.NavigateToSection(NavigationCatalog.DownloadsTag);
        LinkRejected?.Invoke(this, link);
    }

    private void Post(Action action)
    {
        var dispatcher = _window?.DispatcherQueue;
        if (dispatcher is null)
        {
            AppDiagnostics.Write("UI dispatch was unavailable while handling an NXM activation.");
            return;
        }

        if (dispatcher.HasThreadAccess)
            action();
        else if (!dispatcher.TryEnqueue(() => action()))
            AppDiagnostics.Write("UI dispatch rejected an NXM activation callback.");
    }
}
