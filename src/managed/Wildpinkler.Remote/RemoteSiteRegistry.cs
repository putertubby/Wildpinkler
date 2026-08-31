using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Wildpinkler.Remote;

/// <summary>
/// The set of site connectors compiled into the application. Registration happens once at startup;
/// there is no dynamic discovery, so a missing provider is a build error rather than a runtime one.
/// </summary>
public sealed class RemoteSiteRegistry
{
    private readonly List<IRemoteSiteProvider> _providers = new();

    public IReadOnlyList<IRemoteSiteProvider> Providers => new ReadOnlyCollection<IRemoteSiteProvider>(_providers);

    public void Register(IRemoteSiteProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        if (TryGet(provider.SiteId, out _))
            throw new InvalidOperationException($"A provider is already registered for site '{provider.SiteId}'.");
        _providers.Add(provider);
    }

    public bool TryGet(string? siteId, out IRemoteSiteProvider provider)
    {
        provider = _providers.FirstOrDefault(item => string.Equals(item.SiteId, siteId, StringComparison.OrdinalIgnoreCase))!;
        return provider is not null;
    }

    public bool TryGetForScheme(string? scheme, out IRemoteSiteProvider provider)
    {
        provider = _providers.FirstOrDefault(item =>
            string.Equals(item.ProtocolHandler?.Scheme, scheme, StringComparison.OrdinalIgnoreCase))!;
        return provider is not null;
    }

    public bool TryGetForUri(Uri? uri, out IRemoteSiteProvider provider)
    {
        provider = uri is null or { IsAbsoluteUri: false }
            ? null!
            : _providers.FirstOrDefault(item => item.ProtocolHandler?.CanHandle(uri) == true)!;
        return provider is not null;
    }
}
