using System.Linq;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies <see cref="PrerequisiteScanner"/>: the conventional "Prerequisites" heading in a Nexus
/// description yields one name-only prerequisite per bullet, and descriptions without the
/// heading yield none.
/// </summary>
public class PrerequisiteScannerTests
{
    [Fact]
    public void ExtractsBulletsUnderThePrerequisitesHeading()
    {
        var description = """
            [size=6][b]Description[/b][/size]
            <br />
            [size=6][b]Prerequisites[/b][/size]
            <br />
            [list]
            <br />[*]﻿[b]Microsoft C++ 2015-2022 Redistributable[/b]
            <br />[*]﻿[b]SSE (Special Edition) is required[/b]
            <br />
            [size=6][b]Installation[/b][/size]
            """;

        var result = PrerequisiteScanner.Extract(description);

        Assert.Equal(
            [
                "Microsoft C++ 2015-2022 Redistributable",
                "SSE (Special Edition) is required",
            ],
            result.Select(requirement => requirement.ModName).ToList());
        Assert.All(result, requirement => Assert.Equal(RemoteRequirementKind.Prerequisite, requirement.Kind));
        Assert.All(result, requirement => Assert.Null(requirement.ModKey));
    }

    [Fact]
    public void NoHeadingMeansNoPrerequisites()
    {
        var description = """
            [b]Description[/b]
            <br />This mod requires the C++ redistributable.
            <br />No prerequisites heading here.
            """;

        Assert.Empty(PrerequisiteScanner.Extract(description));
    }

    [Fact]
    public void HeadingWithoutBulletsMeansNoPrerequisites()
    {
        var description = """
            [b]Prerequisites[/b]
            <br />Nothing listed under this heading.
            """;

        Assert.Empty(PrerequisiteScanner.Extract(description));
    }

    [Fact]
    public void DuplicateBulletsCollapseToOne()
    {
        var description = """
            [b]Prerequisites[/b]
            <br />[list]
            <br />[*]Microsoft C++ 2015-2022 Redistributable
            <br />[*]microsoft c++ 2015-2022 redistributable
            """;

        Assert.Single(PrerequisiteScanner.Extract(description));
    }
}
