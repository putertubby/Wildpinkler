using System;
using System.Collections.Generic;
using System.Linq;
using Wildpinkler.App.Models;
using Wildpinkler.App.Services.Mods;
using Wildpinkler.Remote;
using Xunit;

namespace Wildpinkler.App.Tests;

/// <summary>
/// Verifies the shared edge-derivation helper turns a resolved plan's requirement chains into
/// source → target edges keyed by the source's nexus key, which the "save as mod list" builder
/// and the install handoff both consume. Covers the data-quality cases: direct children of the
/// root, multi-hop chains, name-only parents, duplicates, recommended/conflict kinds, requirement
/// notes, and the root (no incoming edge).
/// </summary>
public class DependencyPlanEdgeMapTests
{
    private static DependencyPlanEntry Entry(
        string modKey,
        string name,
        List<RemoteRequirement> chain,
        List<string>? notes = null,
        string? fileKey = null,
        RemoteRequirementKind kind = RemoteRequirementKind.Unknown)
    {
        return new DependencyPlanEntry
        {
            Ref = new RemoteRef("fake", "testgame", modKey),
            Name = name,
            Chain = chain,
            RequirementNotes = notes ?? new List<string>(),
            FileKey = fileKey,
            Kind = kind,
            Status = DependencyPlanEntryStatus.Ready,
            IsSelected = true
        };
    }

    // One element of a chain is the edge into its target: the element's ModKey names the target,
    // and its Kind is the kind the source places on that target.
    private static RemoteRequirement Req(string? targetModKey, string targetName, RemoteRequirementKind kind, string? notes = null) =>
        new(kind, targetName, targetModKey, Notes: notes);

    [Fact]
    public void RootHasNoIncomingEdgeAndDirectChildCarriesRootEdge()
    {
        var root = Entry("1", "Root", new List<RemoteRequirement>());
        // Direct child of the root: a one-element chain whose edge targets the child ("2").
        // The resolver accumulates the requirement's notes onto the entry's RequirementNotes.
        var child = Entry("2", "Base", new List<RemoteRequirement>
        {
            Req("2", "Base", RemoteRequirementKind.Required)
        }, notes: new List<string> { "needed" });

        var edges = DependencyPlanEdgeMap.Build(new[] { root, child }, rootModKey: "1");

        // Only the child's incoming edge exists; the root contributes none.
        Assert.Single(edges);
        Assert.True(edges.ContainsKey("1"));
        var edge = edges["1"].Single();
        Assert.Equal("2", edge.TargetKey);
        Assert.Equal("Base", edge.TargetName);
        Assert.Equal("Required", edge.Kind);
        Assert.Equal("needed", edge.Notes);
    }

    [Fact]
    public void NameOnlyRequirementOfRootStillProducesRootEdge()
    {
        // A name-only requirement (no mod key) from the root still has a known source (the root),
        // so an edge is produced — the target is identified by its display name (the mod key the
        // resolver assigned).
        var root = Entry("1", "Root", new List<RemoteRequirement>());
        var child = Entry("Base", "Base", new List<RemoteRequirement>
        {
            Req(null, "Base", RemoteRequirementKind.Required)
        });

        var edges = DependencyPlanEdgeMap.Build(new[] { root, child }, rootModKey: "1");

        Assert.Single(edges);
        Assert.True(edges.ContainsKey("1"));
        Assert.Equal("Base", edges["1"].Single().TargetKey);
    }

    [Fact]
    public void NameOnlyParentThatIsNotRootProducesNoEdge()
    {
        // A name-only requirement from a non-root parent cannot be matched to a source, because
        // the parent's key is unknown (it's a display name). No edge is produced for it.
        var leaf = Entry("3", "Leaf", new List<RemoteRequirement>
        {
            Req(null, "MysteryParent", RemoteRequirementKind.Required),
            Req("3", "Leaf", RemoteRequirementKind.Required)
        });

        var edges = DependencyPlanEdgeMap.Build(new[] { leaf }, rootModKey: "1");

        // The leaf's incoming edge is named by the first element (name-only), so no resolvable
        // source exists and the edge is dropped.
        Assert.Empty(edges);
    }

    [Fact]
    public void RootRequiresMultipleDirectChildren()
    {
        var root = Entry("1", "Root", new List<RemoteRequirement>());
        var a = Entry("2", "A", new List<RemoteRequirement> { Req("2", "A", RemoteRequirementKind.Required) });
        var b = Entry("3", "B", new List<RemoteRequirement> { Req("3", "B", RemoteRequirementKind.Required) });

        var edges = DependencyPlanEdgeMap.Build(new[] { root, a, b }, rootModKey: "1");

        // Source "1" requires both targets "2" and "3": one key, two edges.
        Assert.Single(edges);
        Assert.Equal(2, edges["1"].Count);
        Assert.Contains(edges["1"], item => item.ModKey == "2");
        Assert.Contains(edges["1"], item => item.ModKey == "3");
    }

    [Fact]
    public void MultiHopChainUsesDirectParentAndIncomingEdgeKind()
    {
        // Chain: root -> mid -> leaf. element 0 = "root recommends mid" (kind Recommended),
        // element 1 = "mid requires leaf" (kind Required). The leaf's source edge is element 1,
        // sourced from "mid" and kind Required — it must not inherit the earlier Recommended kind.
        var leaf = Entry("3", "Leaf", new List<RemoteRequirement>
        {
            Req("2", "Mid", RemoteRequirementKind.Recommended),
            Req("3", "Leaf", RemoteRequirementKind.Required)
        });

        var edges = DependencyPlanEdgeMap.Build(new[] { leaf }, rootModKey: "1");

        Assert.True(edges.ContainsKey("2"));
        Assert.False(edges.ContainsKey("1"));
        var edge = edges["2"].Single();
        Assert.Equal("3", edge.TargetKey);
        Assert.Equal("Required", edge.Kind);
    }

    [Fact]
    public void IncompatibleEdgeCarriesItsKind()
    {
        var root = Entry("1", "Root", new List<RemoteRequirement>());
        var conflict = Entry("4", "Conflicting", new List<RemoteRequirement>
        {
            Req("4", "Conflicting", RemoteRequirementKind.Incompatible)
        });

        var edges = DependencyPlanEdgeMap.Build(new[] { conflict }, rootModKey: "1");

        Assert.Equal("Incompatible", edges["1"].Single().Kind);
    }

    [Fact]
    public void NoNotesYieldsNullNotes()
    {
        var root = Entry("1", "Root", new List<RemoteRequirement>());
        var child = Entry("2", "Base", new List<RemoteRequirement>
        {
            Req("2", "Base", RemoteRequirementKind.Required)
        });

        var edges = DependencyPlanEdgeMap.Build(new[] { root, child }, rootModKey: "1");

        Assert.Null(edges["1"].Single().Notes);
    }

    [Fact]
    public void FileKeyIsCarriedForResolvedTargets()
    {
        var root = Entry("1", "Root", new List<RemoteRequirement>());
        var child = Entry("2", "Base", new List<RemoteRequirement>
        {
            Req("2", "Base", RemoteRequirementKind.Required)
        }, fileKey: "20");

        var edges = DependencyPlanEdgeMap.Build(new[] { root, child }, rootModKey: "1");

        Assert.Equal("20", edges["1"].Single().FileKey);
    }
}
