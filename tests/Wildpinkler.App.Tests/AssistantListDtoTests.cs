using System;
using System.Text.Json;
using Wildpinkler.App.Commands;
using Wildpinkler.App.Models;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class AssistantListDtoTests
{
    [Fact]
    public void GameSummaryDto_DoesNotContainInstallPathOrArguments()
    {
        var json = JsonSerializer.Serialize(new GameSummaryDto("game", "Skyrim", 2, true, false));

        Assert.DoesNotContain("InstallPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("LaunchArguments", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileSummaryDto_DoesNotContainFolderPathOrVariables()
    {
        var json = JsonSerializer.Serialize(new ProfileSummaryDto("profile", "Main", "game", "Skyrim", 4, 2, false));

        Assert.DoesNotContain("FolderPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Variables", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"LoadOrder\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ModSummaryDto_DoesNotContainArchivePathHashesOrProfileIds()
    {
        var json = JsonSerializer.Serialize(new ModSummaryDto(
            "mod", "Example", "skyrim", "1.0", "Nexus", "Available", "nexus", "123", 2, DependencyState.Ok));

        Assert.DoesNotContain("ArchivePath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Sha256", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Md5", json, StringComparison.Ordinal);
        Assert.DoesNotContain("ProfileIds", json, StringComparison.Ordinal);
    }
}
