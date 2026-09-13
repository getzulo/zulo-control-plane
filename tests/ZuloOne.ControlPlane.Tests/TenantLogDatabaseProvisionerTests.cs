using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using ZuloOne.ControlPlane.Provisioning;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class TenantLogDatabaseProvisionerTests
{
    [Fact]
    public void Names_follow_slug()
    {
        Assert.Equal("logs_acme", TenantLogNames.Database("acme"));
        Assert.Equal("logs_acme", TenantLogNames.User("acme"));
        Assert.Equal("events", TenantLogNames.Collection);
    }

    [Fact]
    public async Task Empty_operator_url_skips_provisioning()
    {
        var provisioner = CreateProvisioner(null);

        var result = await provisioner.CreateAsync("acme");

        Assert.Null(result);
    }

    [Fact]
    public void Tenant_url_uses_scoped_credentials_and_preserves_cluster_options()
    {
        var provisioner = CreateProvisioner(
            "mongodb://root:secret@mongo1:27017,mongo2:27017/admin?replicaSet=rs0&tls=true");

        var url = new MongoUrl(provisioner.TenantConnectionUrl("logs_acme", "logs_acme", "tenant-secret"));

        Assert.Equal("logs_acme", url.Username);
        Assert.Equal("tenant-secret", url.Password);
        Assert.Equal("logs_acme", url.DatabaseName);
        Assert.Equal("logs_acme", url.AuthenticationSource);
        Assert.Equal("rs0", url.ReplicaSetName);
        Assert.True(url.UseTls);
    }

    private static TenantLogDatabaseProvisioner CreateProvisioner(string? url) =>
        new(
            Options.Create(new TenantLogSettings { Url = url }),
            NullLogger<TenantLogDatabaseProvisioner>.Instance);
}
