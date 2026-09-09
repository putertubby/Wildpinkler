using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Guards the composition root: a missing or miswired registration must fail here rather than at the
/// first navigation in a shipped build.
/// </summary>
public sealed class ServiceRegistrationTests
{
    [Fact]
    public async Task BuildServiceProvider_ValidatesEveryRegistrationOnBuild()
    {
        await using var provider = new ServiceCollection()
            .AddWildpinklerApp()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider);
    }

    [Fact]
    public async Task AddWildpinklerApp_ResolvesEveryRegisteredService()
    {
        var services = new ServiceCollection().AddWildpinklerApp();
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        var serviceTypes = services
            .Select(descriptor => descriptor.ServiceType)
            .Where(type => !type.IsGenericTypeDefinition)
            .Distinct()
            .ToList();

        Assert.NotEmpty(serviceTypes);
        foreach (var serviceType in serviceTypes)
            Assert.NotNull(provider.GetRequiredService(serviceType));
    }

    [Fact]
    public void AddWildpinklerApp_RegistersEveryServiceAsASingleton()
    {
        var services = new ServiceCollection().AddWildpinklerApp();

        var nonSingletons = services
            .Where(descriptor => descriptor.Lifetime != ServiceLifetime.Singleton)
            .Select(descriptor => descriptor.ServiceType.Name)
            .ToList();

        Assert.Empty(nonSingletons);
    }

    [Fact]
    public void AppHost_Get_ThrowsBeforeInitialization()
    {
        if (AppHost.IsInitialized)
            return;

        Assert.Throws<InvalidOperationException>(() => AppHost.Get<ModStore>());
    }
}
