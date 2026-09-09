using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Infra;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Api;

/// <summary>
/// What a database node publishes about itself every five minutes.
/// </summary>
/// <param name="CheckedAt">
/// DateTimeOffset, NOT DateTime, and that is load-bearing. The node sends an ISO
/// timestamp with an offset (<c>2026-09-09T03:08:56.420836+00:00</c>); binding it
/// to DateTime yields Kind=Local, and Npgsql then refuses to write it:
///
///   Cannot write DateTime with Kind=Local to PostgreSQL type 'timestamp with
///   time zone', only UTC is supported.
///
/// The endpoint returned 500 for every real report while a hand-made probe that
/// happened to omit this field succeeded — so the failure looked like a transport
/// problem rather than a binding one.
/// </param>
public record NodeReportRequest(string Node, string Status, string? Report, DateTimeOffset? CheckedAt);

/// <summary>Which node should take the leader role.</summary>
public record SwitchoverRequest(string Candidate, string ConfirmNode);

/// <summary>
/// The cluster underneath the fleet: who leads, who is behind, what each node says
/// about itself, and the one lever worth exposing — handing over the leader role.
/// </summary>
[ApiController]
[Route("api/infra")]
[Produces("application/json")]
public class InfraController : ControllerBase
{
    private readonly ControlPlaneDbContext _db;
    private readonly PatroniClient _patroni;
    private readonly PatroniSettings _settings;
    private readonly ILogger<InfraController> _logger;

    public InfraController(
        ControlPlaneDbContext db, PatroniClient patroni,
        IOptions<PatroniSettings> settings, ILogger<InfraController> logger)
    {
        _db = db;
        _patroni = patroni;
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Cluster state: Patroni's live view, plus each node's own last verdict on the
    /// things Patroni cannot see — etcd quorum, disk, WAL archiving, backup age.
    /// </summary>
    [HttpGet("cluster")]
    public async Task<IActionResult> Cluster(CancellationToken ct)
    {
        var cluster = await _patroni.GetClusterAsync(ct);
        var reports = await _db.NodeHealth.AsNoTracking().ToListAsync(ct);
        var stale = TimeSpan.FromMinutes(Math.Max(1, _settings.ReportStaleAfterMinutes));
        var now = DateTime.UtcNow;

        return Ok(new
        {
            configured = _patroni.IsConfigured,
            // Null when no node answered. Distinct from an empty member list, which
            // would mean Patroni answered and the cluster is genuinely empty.
            reachable = cluster is not null,
            scope = cluster?.Scope,
            members = (cluster?.Members ?? []).Select(m =>
            {
                var report = reports.FirstOrDefault(r =>
                    string.Equals(r.Node, m.Name, StringComparison.OrdinalIgnoreCase));
                var age = report is null ? (TimeSpan?)null : now - report.ReceivedAt;
                return new
                {
                    m.Name,
                    m.Role,
                    m.State,
                    m.Host,
                    m.Port,
                    m.Timeline,
                    m.Lag,
                    m.Lsn,
                    // Silence is a state of its own. A node that stopped reporting is
                    // shown as unheard-from rather than as whatever it last claimed,
                    // because the two are not the same and only one needs attention.
                    selfCheck = report is null ? "never" : age > stale ? "stale" : report.Status,
                    selfCheckAt = report?.ReceivedAt,
                    selfCheckAgeSeconds = age is null ? (double?)null : Math.Round(age.Value.TotalSeconds),
                    selfCheckReport = report?.Report,
                };
            }),
            // Nodes that report but are not Patroni members — the etcd witness, for
            // one. Without this they would simply be invisible.
            unmatchedReports = reports
                .Where(r => cluster is null || !cluster.Members.Any(m =>
                    string.Equals(m.Name, r.Node, StringComparison.OrdinalIgnoreCase)))
                .Select(r => new { r.Node, r.Status, r.ReceivedAt, r.Report }),
        });
    }

    /// <summary>
    /// Where a database node publishes its five-minute self-check.
    ///
    /// <para>
    /// Anonymous by necessity — a Postgres node has no Cloudflare Access session and
    /// no operator account — and guarded by a shared token instead. The token buys
    /// little on its own, with one exception that justifies it: without one, anyone
    /// on the network could post a false "healthy" and mask a real outage.
    /// </para>
    /// </summary>
    [HttpPost("report")]
    [AllowAnonymous]
    public async Task<IActionResult> Report([FromBody] NodeReportRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_settings.ReportToken))
        {
            // Refuse rather than accept anonymously. An unconfigured token must not
            // silently turn into an open endpoint.
            _logger.LogWarning("A node health report arrived but Patroni__ReportToken is not configured — rejecting");
            return StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { error = "Node reporting is not configured on this control plane." });
        }

        var presented = Request.Headers["X-Node-Token"].FirstOrDefault() ?? string.Empty;
        // Fixed-time comparison: this is a secret compared on every five-minute
        // report from every node, which is exactly the shape a timing attack wants.
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(_settings.ReportToken)))
        {
            _logger.LogWarning("Rejected a node health report for {Node} — bad token", request.Node);
            return Unauthorized(new { error = "Bad node token." });
        }

        if (string.IsNullOrWhiteSpace(request.Node))
            return BadRequest(new { error = "A report must name the node it came from." });

        var node = request.Node.Trim();
        var row = await _db.NodeHealth.FirstOrDefaultAsync(n => n.Node == node, ct);
        if (row is null)
        {
            row = new NodeHealth { Node = node };
            _db.NodeHealth.Add(row);
        }

        row.Status = string.IsNullOrWhiteSpace(request.Status) ? "unknown" : request.Status.Trim();
        row.Report = request.Report;
        // .UtcDateTime, so what reaches Npgsql always carries Kind=Utc regardless of
        // the offset the node sent.
        row.CheckedAt = request.CheckedAt?.UtcDateTime ?? DateTime.UtcNow;
        // The control plane's own clock, deliberately. Staleness measured against a
        // timestamp the reporter chose would let a node with a wrong clock — or a
        // stuck one — report itself perpetually fresh.
        row.ReceivedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);
        return Ok(new { received = true });
    }

    /// <summary>
    /// Hands the leader role to another node.
    ///
    /// Every connection to the old leader is dropped, so every tenant sees a brief
    /// error. The node name is retyped for the same reason a tenant's slug is
    /// retyped before deletion: the cost of a misclick is borne by the whole fleet.
    /// </summary>
    [HttpPost("switchover")]
    public async Task<IActionResult> Switchover([FromBody] SwitchoverRequest request, CancellationToken ct)
    {
        var cluster = await _patroni.GetClusterAsync(ct);
        if (cluster is null)
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "No Patroni node answered." });

        var leader = cluster.Members.FirstOrDefault(m =>
            string.Equals(m.Role, "leader", StringComparison.OrdinalIgnoreCase));
        if (leader is null)
            return Conflict(new { error = "The cluster currently has no leader — let Patroni settle before switching over." });

        var candidate = cluster.Members.FirstOrDefault(m =>
            string.Equals(m.Name, request.Candidate, StringComparison.OrdinalIgnoreCase));
        if (candidate is null)
            return BadRequest(new { error = $"'{request.Candidate}' is not a member of this cluster." });
        if (string.Equals(candidate.Name, leader.Name, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"'{candidate.Name}' is already the leader." });

        // Checked BEFORE the state checks would be cheaper, but this order gives the
        // better message: "that node is already the leader" is more useful than
        // "you typed the confirmation wrong".
        if (!string.Equals(request.ConfirmNode, request.Candidate, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Retype '{request.Candidate}' to confirm. This drops every open connection to {leader.Name}." });

        // A replica that is not streaming cannot take over without data loss.
        if (!string.Equals(candidate.State, "streaming", StringComparison.OrdinalIgnoreCase))
            return Conflict(new { error = $"'{candidate.Name}' is '{candidate.State}', not streaming — promoting it now risks losing writes." });

        _logger.LogWarning("Operator {Operator} is switching the leader from {Leader} to {Candidate}",
            User.Identity?.Name ?? "unknown", leader.Name, candidate.Name);

        var (ok, detail) = await _patroni.SwitchoverAsync(leader.Name, candidate.Name, ct);
        return ok
            ? Ok(new { success = true, from = leader.Name, to = candidate.Name, detail })
            : StatusCode(StatusCodes.Status502BadGateway, new { error = detail });
    }
}
