using System;
using System.IO;
using System.Threading.Tasks;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// FOMOD installers are attacker-controlled XML shipped inside mod archives, so the parser must not
/// resolve external entities or follow a DTD.
/// </summary>
public sealed class FomodInstallerParserSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-fomod-" + Guid.NewGuid().ToString("N"));
    private readonly FomodInstallerParser _parser = new();

    public FomodInstallerParserSecurityTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void TryParse_ExternalEntityReferencingALocalFile_DoesNotDiscloseIt()
    {
        var secretPath = Path.Combine(_root, "secret.txt");
        File.WriteAllText(secretPath, "TOP-SECRET-VALUE");

        var xml = $"""
            <?xml version="1.0"?>
            <!DOCTYPE config [ <!ENTITY xxe SYSTEM "file:///{secretPath.Replace('\\', '/')}"> ]>
            <config><moduleName>&xxe;</moduleName></config>
            """;

        var module = _parser.TryParse(xml);

        Assert.True(module is null || !module.Name.Contains("TOP-SECRET-VALUE", StringComparison.Ordinal));
    }

    [Fact]
    public void TryParse_BillionLaughsEntityExpansion_DoesNotExpand()
    {
        var xml = """
            <?xml version="1.0"?>
            <!DOCTYPE config [
              <!ENTITY a "aaaaaaaaaa">
              <!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;">
              <!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">
              <!ENTITY d "&c;&c;&c;&c;&c;&c;&c;&c;&c;&c;">
            ]>
            <config><moduleName>&d;</moduleName></config>
            """;

        var module = _parser.TryParse(xml);

        Assert.True(module is null || module.Name.Length < 1000);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not xml at all")]
    [InlineData("<config>")]
    [InlineData("<unrelated />")]
    public void TryParse_MalformedInput_ReturnsNullInsteadOfThrowing(string xml)
    {
        var module = _parser.TryParse(xml);

        Assert.Null(module);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}

/// <summary>Round-trips credentials to prove the per-install entropy path works end to end.</summary>
public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wp-credentials-" + Guid.NewGuid().ToString("N"));

    public CredentialStoreTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task SetAsync_ThenGetAsync_ReturnsTheStoredValue()
    {
        using var store = new CredentialStore(_root);

        await store.SetAsync("nexus", "secret-key");

        Assert.Equal("secret-key", await store.GetAsync("nexus"));
    }

    [Fact]
    public async Task SetAsync_WritesEntropyAndNeverStoresThePlaintext()
    {
        using var store = new CredentialStore(_root);

        await store.SetAsync("nexus", "secret-key");

        Assert.True(File.Exists(Path.Combine(_root, "credentials.entropy")));
        Assert.DoesNotContain("secret-key", await File.ReadAllTextAsync(Path.Combine(_root, "credentials.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetAsync_WithADifferentInstallEntropy_DoesNotDecrypt()
    {
        using (var store = new CredentialStore(_root))
            await store.SetAsync("nexus", "secret-key");

        // Simulate the file being copied to another installation.
        var other = Path.Combine(_root, "other");
        Directory.CreateDirectory(other);
        File.Copy(Path.Combine(_root, "credentials.json"), Path.Combine(other, "credentials.json"));

        using var foreignStore = new CredentialStore(other);

        Assert.Null(await foreignStore.GetAsync("nexus"));
    }

    [Fact]
    public async Task SetAsync_WithBlankValue_RemovesTheCredential()
    {
        using var store = new CredentialStore(_root);
        await store.SetAsync("nexus", "secret-key");

        await store.SetAsync("nexus", "  ");

        Assert.Null(await store.GetAsync("nexus"));
    }

    [Fact]
    public async Task GetAsync_WithCorruptFile_ReturnsNullInsteadOfThrowing()
    {
        await File.WriteAllTextAsync(Path.Combine(_root, "credentials.json"), "{ not json", TestContext.Current.CancellationToken);
        using var store = new CredentialStore(_root);

        Assert.Null(await store.GetAsync("nexus"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
