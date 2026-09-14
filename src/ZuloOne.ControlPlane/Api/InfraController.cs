using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Infra;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Registry;
using ZuloOne.ControlPlane.Settings;

using ZuloOne.ControlPlane.Auth;

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
public record NodeReportRequest(
    string Node, string Status, string? Report, DateTimeOffset? CheckedAt,
    System.Text.Json.JsonElement? Backups,
    string? Role);

/// <summary>Which node should take the leader role.</summary>
public record SwitchoverRequest(string Candidate, string ConfirmNode);

/// <summary>Ad-hoc pgBackRest: <c>incr</c> (default), <c>diff</c> or <c>full</c>.</summary>
public record ClusterBackupRequestBody(string? Type);

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
    private readonly SettingsStore _store;
    private readonly IJobQueue _queue;
    private readonly ClusterBackupGate _backups;
    private readonly ILogger<InfraController> _logger;

    public InfraController(
        ControlPlaneDbContext db, PatroniClient patroni,
        IOptions<PatroniSettings> settings, SettingsStore store,
        IJobQueue queue, ClusterBackupGate backups,
        ILogger<InfraController> logger)
    {
        _db = db;
        _patroni = patroni;
        _settings = settings.Value;
        _store = store;
        _queue = queue;
        _backups = backups;
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
        var expected = InfraRoles.ParseExpected(_store.List("Infra:ExpectedNodes"));
        var members = cluster?.Members ?? [];

        object DescribeMember(PatroniMember m)
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
        }

        return Ok(new
        {
            configured = _patroni.IsConfigured,
            // Null when no node answered. Distinct from an empty member list, which
            // would mean Patroni answered and the cluster is genuinely empty.
            reachable = cluster is not null,
            scope = cluster?.Scope,
            members = members.Select(DescribeMember),
            nodes = MergeNodes(members, reports, expected, stale, now),
            // Nodes that report but are not Patroni members — the etcd witness, for
            // one. Without this they would simply be invisible.
            unmatchedReports = reports
                .Where(r => !members.Any(m =>
                    string.Equals(m.Name, r.Node, StringComparison.OrdinalIgnoreCase)))
                .Select(r => new { r.Node, r.Status, r.ReceivedAt, r.Report, role = RoleOf(r) }),
            // The CLUSTER's backups, as distinct from the panel's per-tenant dumps.
            // These are what a lost machine is recovered from, and they were
            // invisible here until the node started sending its inventory along.
            backups = SummariseBackups(reports, stale, now, _backups.Peek()),
        });
    }

    /// <summary>
    /// Asks the repository node to take a pgBackRest backup on its next health
    /// check. The panel cannot run that command itself — the repository is a
    /// directory on that machine.
    /// </summary>
    [HttpPost("backup")]
    public async Task<IActionResult> Backup([FromBody] ClusterBackupRequestBody? request, CancellationToken ct)
    {
        var type = ClusterBackupGate.NormalizeType(request?.Type);
        if (type is null)
            return BadRequest(new { error = "Type must be incr, diff or full." });

        var already = await _db.Jobs.AnyAsync(
            j => j.Kind == JobKind.Backup && (j.State == JobState.Queued || j.State == JobState.Running), ct);
        if (already || _backups.Peek() is not null)
            return Conflict(new { error = "A cluster backup is already waiting for the repository node." });

        var holder = await _db.NodeHealth.AsNoTracking()
            .FirstOrDefaultAsync(n => n.BackupsJson != null && n.BackupsJson != "", ct);
        if (holder is null)
            return Conflict(new { error = "No node has reported a backup repository yet. The request has nowhere to go." });

        var job = await _queue.EnqueueAsync(
            JobKind.Backup, tenantId: null, tenantSlug: null,
            new BackupPayload(type, holder.Node), OperatorIdentity.Of(User), ct);
        return Accepted($"/api/jobs/{job.Id}", new { jobId = job.Id });
    }

    /// <summary>
    /// Patroni members, anyone who has reported, and names we were told to expect.
    /// Silence on an expected name is a card of its own — otherwise Mongo never
    /// appearing looks the same as "we do not watch Mongo".
    /// </summary>
    private static List<object> MergeNodes(
        IReadOnlyList<PatroniMember> members,
        List<NodeHealth> reports,
        IReadOnlyList<(string Name, string Role)> expected,
        TimeSpan stale,
        DateTime now)
    {
        var byName = new Dictionary<string, NodeView>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, role) in expected)
            byName[name] = new NodeView { Name = name, Role = role, SelfCheck = "never" };

        foreach (var report in reports)
        {
            var age = now - report.ReceivedAt;
            var view = byName.GetValueOrDefault(report.Node) ?? new NodeView { Name = report.Node };
            view.Role = string.IsNullOrWhiteSpace(report.Role) ? (view.Role.Length > 0 ? view.Role : InfraRoles.Infer(report.Node)) : InfraRoles.Normalize(report.Role);
            view.SelfCheck = age > stale ? "stale" : report.Status;
            view.SelfCheckAt = report.ReceivedAt;
            view.SelfCheckAgeSeconds = Math.Round(age.TotalSeconds);
            view.SelfCheckReport = report.Report;
            byName[report.Node] = view;
        }

        foreach (var member in members)
        {
            var view = byName.GetValueOrDefault(member.Name) ?? new NodeView { Name = member.Name, SelfCheck = "never" };
            if (view.Role.Length == 0 || view.Role == InfraRoles.Host) view.Role = InfraRoles.Postgres;
            view.PatroniRole = member.Role;
            view.State = member.State;
            view.Host = member.Host;
            view.Port = member.Port;
            view.Timeline = member.Timeline;
            view.Lag = member.Lag;
            view.Lsn = member.Lsn;
            byName[member.Name] = view;
        }

        return byName.Values
            .OrderBy(n => Array.IndexOf(InfraRoles.DisplayOrder, n.Role) is var i && i >= 0 ? i : 99)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
            .Select(n => (object)new
            {
                name = n.Name,
                role = n.Role.Length > 0 ? n.Role : InfraRoles.Infer(n.Name),
                selfCheck = n.SelfCheck,
                selfCheckAt = n.SelfCheckAt,
                selfCheckAgeSeconds = n.SelfCheckAgeSeconds,
                selfCheckReport = n.SelfCheckReport,
                patroniRole = n.PatroniRole,
                state = n.State,
                host = n.Host,
                port = n.Port,
                timeline = n.Timeline,
                lag = n.Lag,
                lsn = n.Lsn,
            })
            .ToList();
    }

    private static string RoleOf(NodeHealth report) =>
        string.IsNullOrWhiteSpace(report.Role) ? InfraRoles.Infer(report.Node) : InfraRoles.Normalize(report.Role);

    private sealed class NodeView
    {
        public string Name { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string SelfCheck { get; set; } = "never";
        public DateTime? SelfCheckAt { get; set; }
        public double? SelfCheckAgeSeconds { get; set; }
        public string? SelfCheckReport { get; set; }
        public string? PatroniRole { get; set; }
        public string? State { get; set; }
        public string? Host { get; set; }
        public int Port { get; set; }
        public int? Timeline { get; set; }
        public long? Lag { get; set; }
        public string? Lsn { get; set; }
    }

    /// <summary>
    /// Flattens pgBackRest's inventory into what an operator checks: how old the
    /// newest backup is, and whether anything is wrong with the stanza.
    /// </summary>
    private static object? SummariseBackups(
        List<NodeHealth> reports, TimeSpan stale, DateTime now, ClusterBackupRequest? pending)
    {
        var holder = reports.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.BackupsJson));
        if (holder is null)
        {
            // A request with nowhere to land still has to be visible — otherwise the
            // button looks like it did nothing.
            return pending is null ? null : new
            {
                node = pending.TargetNode,
                count = 0,
                backups = Array.Empty<object>(),
                asOf = pending.RequestedAt,
                asOfStale = false,
                pending = DescribePending(pending),
            };
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(holder.BackupsJson!);
            var stanza = doc.RootElement.EnumerateArray().FirstOrDefault();
            if (stanza.ValueKind != System.Text.Json.JsonValueKind.Object) return null;

            var entries = stanza.TryGetProperty("backup", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Array
                ? b.EnumerateArray().Select(x => new
                {
                    label = x.GetProperty("label").GetString(),
                    type = x.GetProperty("type").GetString(),
                    // Epoch seconds; the panel wants an instant.
                    stopped = DateTimeOffset.FromUnixTimeSeconds(
                        x.GetProperty("timestamp").GetProperty("stop").GetInt64()).UtcDateTime,
                    // `repository.size` is absent on this pgBackRest version —
                    // only delta and size-map exist — so read defensively rather
                    // than assume a shape that varies across releases.
                    sizeBytes = x.TryGetProperty("info", out var i) && i.TryGetProperty("size", out var sz)
                        ? sz.GetInt64() : 0L,
                }).OrderByDescending(x => x.stopped).ToList()
                : [];

            var newest = entries.FirstOrDefault();
            return new
            {
                node = holder.Node,
                stanza = stanza.TryGetProperty("name", out var n) ? n.GetString() : null,
                status = stanza.TryGetProperty("status", out var st) && st.TryGetProperty("message", out var msg)
                    ? msg.GetString() : null,
                count = entries.Count,
                newest = newest is null ? null : new
                {
                    newest.label, newest.type, newest.stopped, newest.sizeBytes,
                    ageHours = Math.Round((now - newest.stopped).TotalHours, 1),
                },
                lastFull = entries.FirstOrDefault(e => e.type == "full")?.stopped,
                backups = entries.Take(10),
                // Read from the node's report, which is itself only as fresh as the
                // last five-minute run — say so rather than imply this is live.
                asOf = holder.ReceivedAt,
                asOfStale = now - holder.ReceivedAt > stale,
                pending = DescribePending(pending),
            };
        }
        catch
        {
            // A shape we cannot read is not a reason to fail the whole page; the
            // node's text report is still shown beside it.
            return pending is null ? null : new
            {
                node = holder.Node,
                count = 0,
                backups = Array.Empty<object>(),
                asOf = holder.ReceivedAt,
                asOfStale = now - holder.ReceivedAt > stale,
                pending = DescribePending(pending),
            };
        }
    }

    private static object? DescribePending(ClusterBackupRequest? pending) =>
        pending is null ? null : new
        {
            type = pending.Type,
            requestedAt = pending.RequestedAt,
            targetNode = pending.TargetNode,
        };

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
        row.Role = string.IsNullOrWhiteSpace(request.Role)
            ? InfraRoles.Infer(node)
            : InfraRoles.Normalize(request.Role);
        row.Report = request.Report;
        // Kept as the node sent it. Only nodes that actually hold the repository
        // report backups, so an absent field leaves the previous answer alone
        // rather than blanking it.
        if (request.Backups is { } backups && backups.ValueKind != System.Text.Json.JsonValueKind.Null)
            row.BackupsJson = backups.GetRawText();
        // .UtcDateTime, so what reaches Npgsql always carries Kind=Utc regardless of
        // the offset the node sent.
        row.CheckedAt = request.CheckedAt?.UtcDateTime ?? DateTime.UtcNow;
        // The control plane's own clock, deliberately. Staleness measured against a
        // timestamp the reporter chose would let a node with a wrong clock — or a
        // stuck one — report itself perpetually fresh.
        row.ReceivedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(ct);

        // Only a script that opted in is given the command. Older check-cluster.sh
        // copies discard the body; claiming here would consume the request into
        // nothing, and the button would look like it worked.
        object? backup = null;
        if (Request.Headers["X-Node-Commands"].Count > 0)
        {
            var claimed = _backups.Claim(node);
            if (claimed is not null)
            {
                _logger.LogInformation(
                    "Handing a {Type} cluster backup to {Node} (asked by {By})",
                    claimed.Type, node, claimed.RequestedBy ?? "unknown");
                backup = new { type = claimed.Type };
            }
        }

        return Ok(new { received = true, backup });
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
            OperatorIdentity.Describe(User), leader.Name, candidate.Name);

        var (ok, detail) = await _patroni.SwitchoverAsync(leader.Name, candidate.Name, ct);
        return ok
            ? Ok(new { success = true, from = leader.Name, to = candidate.Name, detail })
            : StatusCode(StatusCodes.Status502BadGateway, new { error = detail });
    }
}
