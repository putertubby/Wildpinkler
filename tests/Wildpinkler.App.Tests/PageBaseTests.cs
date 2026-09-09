using System.Linq;
using System.Reflection;
using Wildpinkler.App.Pages;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Pages are cached and reused, so the shared base is what guarantees a visit's async work cannot
/// keep writing to controls after the user has moved on. These are structural guards, not UI tests.
/// </summary>
public sealed class PageBaseTests
{
    private static readonly Assembly AppAssembly = typeof(PageBase).Assembly;

    [Fact]
    public void EveryPage_DerivesFromPageBase()
    {
        var pages = AppAssembly.GetTypes()
            .Where(type => type.Namespace == "Wildpinkler.App.Pages" && type.Name.EndsWith("Page", System.StringComparison.Ordinal))
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .ToList();

        Assert.NotEmpty(pages);
        foreach (var page in pages)
            Assert.True(typeof(PageBase).IsAssignableFrom(page), $"{page.Name} does not derive from PageBase.");
    }

    [Fact]
    public void NoPage_DeclaresItsOwnChangeNotificationBoilerplate()
    {
        var offenders = AppAssembly.GetTypes()
            .Where(type => type.Namespace == "Wildpinkler.App.Pages" && typeof(PageBase).IsAssignableFrom(type))
            .Where(type => type != typeof(PageBase))
            .Where(type => type.GetMethod("SetProperty", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly) is not null)
            .Select(type => type.Name)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void PageBase_ExposesANavigationScopedCancellationToken()
    {
        var token = typeof(PageBase).GetProperty("PageToken", BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(token);
        Assert.Equal(typeof(System.Threading.CancellationToken), token!.PropertyType);
    }
}
