using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class ProfileConfigExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesOnlyVariablesAndMountpoints()
    {
        var profile = CreateProfile();
        var target = CreateTarget(steamGameId: "489830");

        try
        {
            var path = await new ProfileConfigExporter().ExportAsync(profile, target);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));

            var topLevelNames = new List<string>();
            foreach (var property in document.RootElement.EnumerateObject())
                topLevelNames.Add(property.Name);
            Assert.Equal(new[] { "variables", "mountpoints" }, topLevelNames);
        }
        finally
        {
            Directory.Delete(profile.FolderPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_VariablesContainOnlyBuiltInsPlusFixedKeys_NoUserVariableLeaks()
    {
        var profile = CreateProfile();
        var target = CreateTarget(steamGameId: "489830");

        try
        {
            var path = await new ProfileConfigExporter().ExportAsync(profile, target);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var variables = document.RootElement.GetProperty("variables");

            Assert.Equal("${InstallPath}", variables.GetProperty("workingDirectory").GetString());
            Assert.Equal("C:\\games\\test", variables.GetProperty("InstallPath").GetString());
            Assert.False(variables.TryGetProperty("targetPath", out _));
            Assert.False(variables.TryGetProperty("arguments", out _));
            Assert.False(variables.TryGetProperty("steamGameId", out _));
            Assert.False(variables.TryGetProperty("UserDefinedVariable", out _));
            Assert.False(variables.TryGetProperty("Documents", out _));
        }
        finally
        {
            Directory.Delete(profile.FolderPath, recursive: true);
        }
    }

    [Fact]
    public async Task ExportAsync_WritesMountpointShape()
    {
        var profile = CreateProfile();
        var target = CreateTarget(steamGameId: string.Empty);

        try
        {
            var path = await new ProfileConfigExporter().ExportAsync(profile, target);
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
            var mountpoint = document.RootElement.GetProperty("mountpoints")[0];

            Assert.Equal("GameInstall", mountpoint.GetProperty("name").GetString());
            Assert.Equal("${InstallPath}", mountpoint.GetProperty("root").GetString());
            Assert.Equal("C:\\profiles\\profile\\overlay", mountpoint.GetProperty("branches")[0].GetString());
            Assert.True(mountpoint.GetProperty("writable").GetBoolean());
        }
        finally
        {
            Directory.Delete(profile.FolderPath, recursive: true);
        }
    }

    private static Profile CreateProfile() => new()
    {
        Id = "profile",
        Name = "Profile",
        GameId = "game-id",
        FolderPath = Path.Combine(Path.GetTempPath(), "wp-export-tests-" + Guid.NewGuid())
    };

    private static LaunchTarget CreateTarget(string steamGameId)
    {
        var variables = new Dictionary<string, string>
        {
            ["InstallPath"] = "C:\\games\\test",
            ["Documents"] = "C:\\Users\\test\\Documents",
            ["UserDefinedVariable"] = "should-not-be-exported"
        };
        var builtInNames = new[] { "InstallPath", "Documents" };

        var view = new MergedView
        {
            Name = "GameInstall",
            MountPath = "C:\\games\\test",
            Branches = new List<string> { "C:\\profiles\\profile\\overlay" },
            IsWritable = true
        };

        // Real (on-disk) paths point into the mod's own install folder; virtual paths are where the
        // game sees the same file once the mod branch is mounted at the install root - the two must
        // differ here so a test regression can't hide behind them accidentally being equal.
        return new LaunchTarget(
            "game",
            "Test game",
            LaunchTargetKind.Game,
            "C:\\mods\\launcher\\loader.exe",
            "-skse",
            "C:\\mods\\launcher",
            "C:\\games\\test\\loader.exe",
            "C:\\games\\test",
            new[] { view },
            variables,
            builtInNames,
            "profile.json",
            false,
            steamGameId);
    }
}
