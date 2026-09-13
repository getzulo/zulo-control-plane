using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>
/// Schema + entity types + scripts on a tenant that already has the models.
/// </summary>
/// <remarks>
/// Install-tree used to compile scripts without generating types. Re-pushing
/// the tree is the wrong fix — it hits FKs and rolls back Registers. This job
/// only calls APIs the running image already has.
/// </remarks>
public sealed class CompileModelsJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly TenantApiClient _tenants;
    private readonly FleetConfig _fleet;

    public CompileModelsJobHandler(ControlPlaneDbContext db, TenantApiClient tenants, FleetConfig fleet)
    {
        _db = db;
        _tenants = tenants;
        _fleet = fleet;
    }

    public JobKind Kind => JobKind.CompileModels;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var id = context.Job.TenantId ?? throw new InvalidOperationException("A CompileModels job must name a tenant.");
        var tenant = await _db.Tenants.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new InvalidOperationException($"Tenant {id} is no longer in the registry.");

        await context.StepAsync($"Schema, entity types, then scripts on {tenant.Slug}", 20, ct);
        var result = await _tenants.MaterializeAsync(
            tenant, TimeSpan.FromSeconds(Math.Max(_fleet.ReadinessTimeoutSeconds, 300)), ct);

        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                $"{tenant.Slug} did not compile: {string.Join(" | ", result.Errors.Take(5))}");
        }

        if (result.CompilationProblems.Count > 0)
        {
            throw new InvalidOperationException(
                $"{result.CompilationProblems.Count} model(s) still do not compile: "
                + string.Join(" | ", result.CompilationProblems.Take(5)));
        }

        await context.StepAsync($"{tenant.Slug} compiled", 100, ct);
    }
}
