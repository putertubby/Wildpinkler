using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Wildpinkler.App.Services;
using WinRT;

namespace Wildpinkler.App;

public static class Program
{
    private const uint RedirectTimeoutMilliseconds = 10_000;

    private static Uri? _initialActivationUri;

    public static Uri? TakeInitialActivationUri() => Interlocked.Exchange(ref _initialActivationUri, null);

    [STAThread]
    public static void Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();
        AppDiagnostics.Initialize();
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var instance = AppInstance.FindOrRegisterForKey("Wildpinkler");
        if (!instance.IsCurrent)
        {
            RedirectActivation(activation, instance);
            return;
        }

        _initialActivationUri = NxmActivationResolver.FromProcessArguments(args);
        Application.Start(_ =>
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcherQueue));
            App application = new();
            GC.KeepAlive(application);
        });
    }

    /// <summary>
    /// Runs only in the redundant instance, which hands its activation to the running one and exits.
    /// The wait has to pump COM messages, so <c>CoWaitForMultipleObjects</c> is used instead of a plain
    /// await: the redirect completes through this STA thread's message loop and would otherwise deadlock.
    /// </summary>
    private static void RedirectActivation(AppActivationArguments activation, AppInstance instance)
    {
        var completed = new ManualResetEvent(false);
        Exception? redirectError = null;
        var redirectTask = Task.Run(async () =>
        {
            try
            {
                await instance.RedirectActivationToAsync(activation);
            }
            catch (Exception exception)
            {
                redirectError = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        var handles = new[] { completed.SafeWaitHandle.DangerousGetHandle() };
        var waitResult = CoWaitForMultipleObjects(0, RedirectTimeoutMilliseconds, 1, handles, out _);
        if (waitResult != 0)
        {
            AppDiagnostics.Write("Activation redirection timed out or failed while waiting.");
            _ = redirectTask.ContinueWith(_ => completed.Dispose(), TaskScheduler.Default);
            return;
        }

        completed.Dispose();
        if (redirectError is not null)
            AppDiagnostics.Write("Activation redirection failed.", redirectError);
    }

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint flags,
        uint timeoutMilliseconds,
        ulong handleCount,
        IntPtr[] handles,
        out uint index);
}