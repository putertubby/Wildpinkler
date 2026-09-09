using System;
using System.Threading.Tasks;
using Wildpinkler.App.Services;

namespace Wildpinkler.App;

/// <summary>
/// Runs the body of a XAML event handler. Handlers have to return void, which means an exception in
/// an <c>async void</c> body is unobserved: it bypasses every local catch and surfaces as a crash.
/// Routing them through here keeps the failure attached to the operation that caused it.
/// </summary>
internal static class UiTask
{
    /// <param name="report">Shows the failure where the user is looking, if the surface can.</param>
    public static async void Run(Func<Task> operation, string operationName, Action<Exception>? report = null)
    {
        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
            // The user navigated away or cancelled; nothing to report.
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write($"{operationName} failed.", exception);
            report?.Invoke(exception);
        }
    }
}
