using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Wildpinkler.App.Commands;

namespace Wildpinkler.App.Services;

/// <summary>
/// Asks the user to approve a destructive command. Everything routes through here, including agent
/// tool calls, so an assistant can never delete a profile without an explicit click.
/// </summary>
public sealed class DialogCommandConfirmation : IAppCommandConfirmation
{
    private readonly Func<XamlRoot?> _rootAccessor;
    private readonly DispatcherQueue? _dispatcher;

    public DialogCommandConfirmation(Func<XamlRoot?> rootAccessor, DispatcherQueue? dispatcher)
    {
        _rootAccessor = rootAccessor;
        _dispatcher = dispatcher;
    }

    public async Task<bool> ConfirmAsync(AppCommandDescriptor descriptor, string summary, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        var root = _rootAccessor();
        // Without a window there is nobody to ask, and silently proceeding would be the wrong default.
        if (root is null)
            return false;

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Show() => _ = ShowAsync(descriptor, summary, root, completion);

        if (_dispatcher is null || _dispatcher.HasThreadAccess)
            Show();
        else if (!_dispatcher.TryEnqueue(Show))
            return false;

        using (cancellationToken.Register(() => completion.TrySetResult(false)))
            return await completion.Task;
    }

    private static async Task ShowAsync(AppCommandDescriptor descriptor, string summary, XamlRoot root, TaskCompletionSource<bool> completion)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = descriptor.IsDestructive ? "This cannot be undone" : "Confirm",
                Content = $"{descriptor.Description}\n\n{summary}",
                PrimaryButtonText = descriptor.IsDestructive ? "Delete" : "Continue",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            completion.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary);
        }
        catch (Exception exception)
        {
            AppDiagnostics.Write($"Showing the confirmation for '{descriptor.Name}' failed.", exception);
            completion.TrySetResult(false);
        }
    }
}
