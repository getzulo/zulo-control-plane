using ZuloOne.ControlPlane.Infra;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class InfraRolesTests
{
    [Fact]
    public void ParseExpected_reads_name_and_role()
    {
        var parsed = InfraRoles.ParseExpected(
            ["zo-pg-1:postgres", "mongo:mongo", "zo-pgw-1:etcd", "zo-app-1:app"]);
        Assert.Equal(
            [("zo-pg-1", "postgres"), ("mongo", "mongo"), ("zo-pgw-1", "etcd"), ("zo-app-1", "app")],
            parsed);
    }

    [Fact]
    public void ParseExpected_infers_role_when_the_suffix_is_missing()
    {
        var parsed = InfraRoles.ParseExpected(["zo-cp-1", "zo-ci-1"]);
        Assert.Equal("panel", parsed[0].Role);
        Assert.Equal("ci", parsed[1].Role);
    }

    [Fact]
    public void Normalize_aliases()
    {
        Assert.Equal("postgres", InfraRoles.Normalize("patroni"));
        Assert.Equal("panel", InfraRoles.Normalize("control-plane"));
        Assert.Equal("etcd", InfraRoles.Normalize("witness"));
    }
}
