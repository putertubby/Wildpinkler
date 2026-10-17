using System.Threading;
using System.Threading.Tasks;

namespace Wildpinkler.Remote;

/// <summary>
/// Optional capability: a site that exposes collections (curated, revision-pinned lists of mod
/// files). A provider declares it through
/// <see cref="RemoteSiteCapabilities.SupportsCollections"/>; consumers must check the capability
/// before casting to this interface.
/// </summary>
public interface IRemoteCollectionProvider
{
    /// <summary>
    /// Fetches a collection pinned to an optional revision. The returned collection may contain
    /// nested sub-collection entries; the caller is responsible for resolving them.
    /// </summary>
    Task<RemoteCollection> GetCollectionAsync(RemoteCollectionRef reference, RemoteCredential credential, CancellationToken cancellationToken = default);
}
