using System;
using System.IO;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public class GameVersionInspectorTests
{
    [Fact]
    public void ReadVersion_MissingFile_ReturnsNull() =>
        Assert.Null(GameVersionInspector.ReadVersion(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));

    [Fact]
    public void ReadVersion_EmptyPath_ReturnsNull() =>
        Assert.Null(GameVersionInspector.ReadVersion(string.Empty));

    [Fact]
    public void ReadVersion_KnownSystemBinary_ReturnsNonEmptyVersion()
    {
        // notepad.exe is present on every Windows install this app targets and always carries a version resource.
        var notepad = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        if (!File.Exists(notepad))
            return; // Environment without notepad.exe (e.g. Server Core) - nothing meaningful to assert.

        Assert.False(string.IsNullOrWhiteSpace(GameVersionInspector.ReadVersion(notepad)));
    }
}
