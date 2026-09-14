using ZuloOne.ControlPlane.Infra;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class NodePruneGateTests
{
    [Fact]
    public void TryRequest_refuses_a_second_for_the_same_node()
    {
        var gate = new NodePruneGate();
        Assert.True(gate.TryRequest("zo-ci-1"));
        Assert.False(gate.TryRequest("zo-ci-1"));
        Assert.True(gate.IsPending("zo-ci-1"));
    }

    [Fact]
    public void Two_nodes_can_wait_at_once()
    {
        var gate = new NodePruneGate();
        Assert.True(gate.TryRequest("zo-ci-1"));
        Assert.True(gate.TryRequest("zo-app-1"));
        Assert.True(gate.Claim("zo-ci-1"));
        Assert.True(gate.IsPending("zo-app-1"));
        Assert.False(gate.IsPending("zo-ci-1"));
    }
}
