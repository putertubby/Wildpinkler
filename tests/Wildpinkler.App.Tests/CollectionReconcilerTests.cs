using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Wildpinkler.App.Services;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class CollectionReconcilerTests
{
    [Fact]
    public void Reconcile_AddsRemovesAndReordersByKey()
    {
        var retained = new Row("b", "old b");
        var target = new ObservableCollection<Row>
        {
            new("a", "a"),
            retained,
            new("c", "c")
        };
        var source = new[]
        {
            new Row("c", "updated c"),
            new Row("b", "updated b"),
            new Row("d", "d")
        };

        CollectionReconciler.Reconcile(target, source, row => row.Id);

        Assert.Equal(new[] { "c", "b", "d" }, target.Select(row => row.Id));
        Assert.DoesNotContain(target, row => row.Id == "a");
        Assert.Same(retained, target[1]);
        Assert.Equal("old b", target[1].Value);
    }

    [Fact]
    public void Reconcile_NoOpDoesNotRaiseCollectionChanges()
    {
        var first = new Row("a", "a");
        var second = new Row("b", "b");
        var target = new ObservableCollection<Row> { first, second };
        var changeCount = 0;
        target.CollectionChanged += (_, _) => changeCount++;

        CollectionReconciler.Reconcile(target, new[] { first, second }, row => row.Id);

        Assert.Equal(0, changeCount);
        Assert.Same(first, target[0]);
        Assert.Same(second, target[1]);
    }

    [Fact]
    public void Reconcile_UsesMoveForExistingItems()
    {
        var target = new ObservableCollection<Row>
        {
            new("a", "a"),
            new("b", "b"),
            new("c", "c")
        };
        var actions = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => actions.Add(args.Action);

        CollectionReconciler.Reconcile(target, new[] { target[2], target[0], target[1] }, row => row.Id);

        Assert.Equal(new[] { "c", "a", "b" }, target.Select(row => row.Id));
        Assert.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Move, actions);
        Assert.DoesNotContain(System.Collections.Specialized.NotifyCollectionChangedAction.Reset, actions);
    }

    [Fact]
    public void Reconcile_DuplicateSourceKeys_AreRejected()
    {
        var target = new ObservableCollection<Row> { new("a", "a") };

        Assert.Throws<ArgumentException>(() => CollectionReconciler.Reconcile(
            target,
            new[] { new Row("b", "first"), new Row("b", "second") },
            row => row.Id));
    }

    private sealed record Row(string Id, string Value);
}
