using System;
using System.Threading.Tasks;
using Wildpinkler.Remote;

namespace Wildpinkler.App.Services;

/// <summary>
/// Turns a protocol activation into a parsed link. A URI no provider claims, or one a provider
/// cannot download, is reported rather than dropped.
/// </summary>
public sealed class RemoteActivationRouter
{
    private readonly RemoteSiteRegistry _registry;

    public RemoteActivationRouter(RemoteSiteRegistry registry) => _registry = registry;

    public event EventHandler<RemoteLink>? LinkReceived;

    public event EventHandler<Uri>? UnknownSchemeReceived;

    public Task RouteAsync(Uri? uri)
    {
        if (uri is null)
        {
            AppDiagnostics.Write("Activation carried no resolvable nxm URI.");
            return Task.CompletedTask;
        }

        if (!_registry.TryGetForUri(uri, out var provider) || provider.ProtocolHandler is null)
        {
            UnknownSchemeReceived?.Invoke(this, uri);
            return Task.CompletedTask;
        }

        LinkReceived?.Invoke(this, provider.ProtocolHandler.Parse(uri));
        return Task.CompletedTask;
    }
}
