using System;

namespace Wildpinkler.Remote;

/// <summary>Parses a site's custom URI scheme, for example <c>nxm:</c>.</summary>
public interface IRemoteProtocolHandler
{
    string SiteId { get; }

    /// <summary>The scheme without a colon, for example <c>nxm</c>.</summary>
    string Scheme { get; }

    /// <summary>True when the URI belongs to this handler's scheme.</summary>
    bool CanHandle(Uri? uri);

    /// <summary>
    /// Parses a URI this handler claims. Never returns null: an unrecognised shape comes back as
    /// <see cref="RemoteLinkKind.Unsupported"/> carrying a reason to show the user.
    /// </summary>
    RemoteLink Parse(Uri uri);
}

/// <summary>Owns the machine-level registration that makes a scheme launch this application.</summary>
public interface IRemoteProtocolRegistrar
{
    string Scheme { get; }

    bool IsRegistered();

    /// <summary>The command currently registered for the scheme, or null when nothing owns it.</summary>
    string? GetCurrentOwnerCommand();

    void Register();

    /// <summary>Restores the previously registered handler when one was displaced.</summary>
    void Unregister();
}
