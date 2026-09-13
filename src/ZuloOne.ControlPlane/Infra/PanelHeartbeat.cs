using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Provisioning;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;

namespace ZuloOne.ControlPlane.Infra;

/// <summary>
/// The panel publishes its own five-minute check, same channel as the other
/// machines. Without this it would be the one host that can never look stale,
/// because it is the thing that measures staleness.
/// </summary>
public sealed class PanelHeartbeat : BackgroundService
{
    private readonly IServiceScopeFactory _scopes;
    private readonly SettingsStore _settings;
    private readonly TenantLogStore _logs;
    private readonly ILogger<PanelHeartbeat> _logger;

    public PanelHeartbeat(
        IServiceScopeFactory scopes,
        SettingsStore settings,
        TenantLogStore logs,
        ILogger<PanelHeartbeat> logger)
    {
        _scopes = scopes;
        _settings = settings;
        _logs = logs;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PublishAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Panel heartbeat failed; trying again in 5 minutes");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task PublishAsync(CancellationToken ct)
    {
        var expected = InfraRoles.ParseExpected(_settings.List("Infra:ExpectedNodes"));
        var name = expected.FirstOrDefault(x => x.Role == InfraRoles.Panel).Name;
        if (string.IsNullOrWhiteSpace(name))
            name = Environment.MachineName;

        var worst = 0;
        var lines = new List<string>
        {
            $"ZuloOne panel check — {name} — {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}Z",
        };
        void Ok(string m) => lines.Add($"  [ ok ]   {m}");
        void Warn(string m) { lines.Add($"  [ WARN ] {m}"); if (worst < 1) worst = 1; }

        Ok("process is answering");

        if (!_logs.IsConfigured)
        {
            Warn("Mongo journal is not configured — tenant Logs stay empty");
        }
        else
        {
            var status = await _logs.StatusAsync([], ct);
            if (status.State == "connected") Ok("Mongo journal is reachable");
            else Warn($"Mongo journal: {status.Error ?? "disconnected"}");
        }

        lines.Add(worst == 0 ? "RESULT: healthy" : "RESULT: DEGRADED");
        var statusName = worst == 0 ? "healthy" : "degraded";

        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ControlPlaneDbContext>();
        var row = await db.NodeHealth.FirstOrDefaultAsync(n => n.Node == name, ct);
        if (row is null)
        {
            row = new NodeHealth { Node = name };
            db.NodeHealth.Add(row);
        }
        row.Role = InfraRoles.Panel;
        row.Status = statusName;
        row.Report = string.Join('\n', lines);
        row.CheckedAt = DateTime.UtcNow;
        row.ReceivedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
    }
}
