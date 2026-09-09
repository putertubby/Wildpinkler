using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;

namespace Wildpinkler.App.Services;

/// <summary>
/// Holds the process-wide container. Pages and dialogs resolve through <see cref="AppServices"/>
/// until they take their dependencies by constructor; new code should inject instead.
/// </summary>
public static class AppHost
{
    private static ServiceProvider? _provider;

    public static IServiceProvider Services =>
        _provider ?? throw new InvalidOperationException("The service container has not been built yet.");

    public static bool IsInitialized => _provider is not null;

    public static IServiceProvider Initialize(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddWildpinklerApp();
        configure?.Invoke(services);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
        return _provider;
    }

    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    /// <summary>
    /// Must be asynchronous: the container owns at least one <see cref="IAsyncDisposable"/> singleton,
    /// and a synchronous <c>Dispose</c> on such a provider throws.
    /// </summary>
    public static async ValueTask ShutdownAsync()
    {
        var provider = _provider;
        _provider = null;
        if (provider is not null)
            await provider.DisposeAsync();
    }
}
