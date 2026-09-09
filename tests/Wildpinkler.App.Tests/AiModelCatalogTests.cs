using System;
using System.Linq;
using System.Threading.Tasks;
using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AiModelCatalogTests
{
    [Fact]
    public void ParseModels_OpenAiShape_ReturnsSortedDistinctIds()
    {
        const string json = """
            {"object":"list","data":[
                {"id":"gpt-4o-mini"},
                {"id":"gpt-4o"},
                {"id":"GPT-4O"}
            ]}
            """;

        Assert.Equal(["gpt-4o", "gpt-4o-mini"], AiModelCatalog.ParseModels(json));
    }

    [Fact]
    public void ParseModels_MalformedJson_ReturnsEmpty() =>
        Assert.Empty(AiModelCatalog.ParseModels("{ not json"));

    [Fact]
    public void ParseModels_MissingDataArray_ReturnsEmpty() =>
        Assert.Empty(AiModelCatalog.ParseModels("""{"models":["a"]}"""));

    [Fact]
    public void ParseModels_DataIsNotAnArray_ReturnsEmpty() =>
        Assert.Empty(AiModelCatalog.ParseModels("""{"data":"everything"}"""));

    [Fact]
    public void ParseModels_EntriesWithoutAStringId_AreSkipped()
    {
        const string json = """
            {"data":[{"id":"good"},{"id":42},{"name":"no-id"},"scalar",null]}
            """;

        Assert.Equal("good", Assert.Single(AiModelCatalog.ParseModels(json)));
    }

    [Fact]
    public void ParseModels_BlankIds_AreSkipped() =>
        Assert.Empty(AiModelCatalog.ParseModels("""{"data":[{"id":"  "},{"id":""}]}"""));

    [Fact]
    public async Task IsReachableAsync_NoEndpoint_ReturnsFalse()
    {
        using var catalog = new AiModelCatalog();
        var configuration = new AiConfiguration("custom", null, "any", false, null);

        Assert.False(await catalog.IsReachableAsync(configuration, TestContext.Current.CancellationToken));
    }
}
