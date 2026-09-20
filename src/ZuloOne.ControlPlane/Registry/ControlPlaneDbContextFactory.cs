using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;

namespace ZuloOne.ControlPlane.Registry;

/// <summary>
/// Lets <c>dotnet ef</c> build a context without starting the application.
/// </summary>
/// <remarks>
/// <para>
/// Without this, every EF command fails: the tooling falls back to constructing
/// the app's host, <c>Program.cs</c> throws because <c>ConnectionStrings:ControlPlane</c>
/// is absent on a developer machine, and the error it reports —
/// "Unable to resolve service for type DbContextOptions" — names neither cause.
/// </para>
/// <para>
/// That mattered more than it sounds. Migrations here are written by hand and the
/// model snapshot is maintained by hand with them, so
/// <c>dotnet ef migrations has-pending-model-changes</c> is the only mechanical
/// check that the two agree. Without a factory that check could not run at all,
/// and a snapshot that had drifted would stay silent until the NEXT migration was
/// generated against it — which is the expensive place to find out.
/// </para>
/// <para>
/// The placeholder connection string is deliberate. Schema-only commands
/// (<c>migrations list --no-connect</c>, <c>has-pending-model-changes</c>,
/// <c>migrations script</c>) never open a connection; they only need a provider
/// so that column types resolve the same way they will in production. Commands
/// that DO touch a database (<c>database update</c>) still need a real
/// <c>ConnectionStrings__ControlPlane</c> in the environment, and will fail
/// plainly against the placeholder rather than doing something unexpected.
/// </para>
/// </remarks>
public class ControlPlaneDbContextFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    private const string Placeholder =
        "Host=localhost;Port=5432;Database=zuloone_controlplane_designtime;Username=postgres;Password=postgres";

    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.Development.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var connection = configuration.GetConnectionString("ControlPlane") ?? Placeholder;

        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connection)
            .Options;

        return new ControlPlaneDbContext(options);
    }
}
