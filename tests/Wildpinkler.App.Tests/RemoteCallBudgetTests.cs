using Wildpinkler.App.Agent;
using Xunit;

namespace Wildpinkler.App.Tests;

public sealed class RemoteCallBudgetTests
{
    [Fact]
    public void TryConsume_ThreeCalls_SucceedsAndFourthFails()
    {
        var budget = new RemoteCallBudget();

        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.True(budget.TryConsume());
        Assert.False(budget.TryConsume());
    }

    [Fact]
    public void BeginTurn_ResetsTheBudget()
    {
        var budget = new RemoteCallBudget();
        for (var index = 0; index < RemoteCallBudget.MaxCallsPerTurn; index++)
            Assert.True(budget.TryConsume());

        budget.BeginTurn();

        Assert.True(budget.TryConsume());
    }
}
