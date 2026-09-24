using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning.Demo;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Jobs;

/// <summary>Snapshots the golden tenant and blesses the dump as the pool template.</summary>
public sealed class DemoTemplateJobHandler : IJobHandler
{
    private readonly ControlPlaneDbContext _db;
    private readonly SnapshotWriter _writer;
    private readonly DemoConfig _config;
    private readonly SettingsStore _store;
    private readonly DemoNudge _nudge;

    public DemoTemplateJobHandler(
        ControlPlaneDbContext db,
        SnapshotWriter writer,
        DemoConfig config,
        SettingsStore store,
        DemoNudge nudge)
    {
        _db = db;
        _writer = writer;
        _config = config;
        _store = store;
        _nudge = nudge;
    }

    public JobKind Kind => JobKind.DemoTemplate;

    public async Task RunAsync(JobContext context, CancellationToken ct)
    {
        var golden = await _db.Tenants.FirstOrDefaultAsync(t => t.Slug == _config.GoldenSlug, ct)
            ?? throw new InvalidOperationException($"Golden tenant '{_config.GoldenSlug}' is not registered.");
        if (golden.Status != TenantStatus.Active || string.IsNullOrWhiteSpace(golden.DatabaseName))
            throw new InvalidOperationException($"Golden tenant '{golden.Slug}' is not active.");

        golden.Demo = TenantDemo.Golden;
        golden.ExpiresAt = null;
        golden.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        var snapshot = await _writer.WriteAsync(
            context, golden, SnapshotKind.DemoTemplate,
            $"Blessed demo template from {golden.Slug}", ct);

        const string key = "Demo:TemplateSnapshotId";
        var row = await _db.Settings.FirstOrDefaultAsync(s => s.Key == key, ct);
        if (row is null)
        {
            _db.Settings.Add(new Setting
            {
                Key = key,
                Value = snapshot.Id.ToString(),
                UpdatedAt = DateTime.UtcNow,
                UpdatedBy = "system:demo-template",
            });
        }
        else
        {
            row.Value = snapshot.Id.ToString();
            row.UpdatedAt = DateTime.UtcNow;
            row.UpdatedBy = "system:demo-template";
        }

        // Only unclaimed workspaces are stale now. A claimed demo is a promise
        // to a person and keeps running until its own expiry.
        await _db.Tenants
            .Where(t => t.Demo == TenantDemo.Pooled)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.ExpiresAt, DateTime.UtcNow)
                .SetProperty(t => t.UpdatedAt, DateTime.UtcNow), ct);
        await _db.SaveChangesAsync(ct);
        await _store.ReloadAsync(ct);
        _nudge.Signal();

        await context.StepAsync($"Template {snapshot.Id} is active", 100, ct);
    }
}
