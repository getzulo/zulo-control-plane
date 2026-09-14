using ZuloOne.ControlPlane.Infra;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class ClusterBackupGateTests
{
    [Theory]
    [InlineData(null, "incr")]
    [InlineData("INCR", "incr")]
    [InlineData("diff", "diff")]
    [InlineData("full", "full")]
    [InlineData("weekly", null)]
    public void NormalizeType_accepts_only_pgbackrest_kinds(string? input, string? expected)
        => Assert.Equal(expected, ClusterBackupGate.NormalizeType(input));

    [Fact]
    public void TryRequest_refuses_a_second_while_one_is_waiting()
    {
        var gate = new ClusterBackupGate();
        Assert.True(gate.TryRequest("incr", "op", "zo-pg-1"));
        Assert.False(gate.TryRequest("full", "op", "zo-pg-1"));
        Assert.Equal("incr", gate.Peek()?.Type);
    }

    [Fact]
    public void Claim_only_the_target_node_gets_the_command()
    {
        var gate = new ClusterBackupGate();
        Assert.True(gate.TryRequest("diff", "op", "zo-pg-1"));
        Assert.Null(gate.Claim("zo-pg-2"));
        var claimed = gate.Claim("zo-pg-1");
        Assert.Equal("diff", claimed?.Type);
        Assert.Null(gate.Peek());
    }
}
