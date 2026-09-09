using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace ZuloOne.ControlPlane.Infra;

/// <summary>One node as Patroni describes it.</summary>
public sealed record PatroniMember(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("host")] string? Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("timeline")] int? Timeline,
    // Replicas only — the leader has nothing to lag behind.
    [property: JsonPropertyName("lag")] long? Lag,
    [property: JsonPropertyName("lsn")] string? Lsn);

public sealed record PatroniCluster(
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("members")] IReadOnlyList<PatroniMember> Members);

/// <summary>
/// Reads cluster state from Patroni, and hands the leader role to another node.
///
/// <para>
/// Reads need no credential: Patroni protects only PUT/POST/PATCH/DELETE, so
/// <c>GET /cluster</c> is open and the infrastructure screen works even where the
/// password is not configured. Writes need both the credential AND a source address
/// on the node's <c>restapi.allowlist</c> — the panel is on it, the CI runner
/// deliberately is not.
/// </para>
/// </summary>
public sealed class PatroniClient
{
    private readonly IHttpClientFactory _http;
    private readonly PatroniSettings _settings;
    private readonly ILogger<PatroniClient> _logger;

    public PatroniClient(IHttpClientFactory http, IOptions<PatroniSettings> settings, ILogger<PatroniClient> logger)
    {
        _http = http;
        _settings = settings.Value;
        _logger = logger;
    }

    public bool IsConfigured => _settings.Nodes.Length > 0;

    /// <summary>
    /// Cluster state from whichever node answers first.
    ///
    /// Every member reports the same view, so there is no "right" node to ask — and
    /// asking only the leader would mean losing all visibility at exactly the moment
    /// the leader is the thing that broke.
    /// </summary>
    public async Task<PatroniCluster?> GetClusterAsync(CancellationToken ct = default)
    {
        using var client = _http.CreateClient("patroni");
        foreach (var node in _settings.Nodes)
        {
            try
            {
                var json = await client.GetStringAsync($"{node.TrimEnd('/')}/cluster", ct);
                var cluster = JsonSerializer.Deserialize<PatroniCluster>(json);
                if (cluster is not null) return cluster;
            }
            catch (Exception ex)
            {
                // Expected while a node is down — that is the case this loop exists
                // for. Only worth a debug line; the caller sees the null.
                _logger.LogDebug(ex, "Patroni node {Node} did not answer", node);
            }
        }
        return null;
    }

    /// <summary>
    /// Moves the leader role to <paramref name="candidate"/>. Brief interruption:
    /// every open connection to the old leader is dropped.
    /// </summary>
    /// <returns>Patroni's own response text, which names what it did or refused to do.</returns>
    public async Task<(bool Ok, string Detail)> SwitchoverAsync(string leader, string candidate, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.Username) || string.IsNullOrWhiteSpace(_settings.Password))
            return (false, "No Patroni credentials are configured, so unsafe endpoints cannot be called.");

        using var client = _http.CreateClient("patroni");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{_settings.Username}:{_settings.Password}")));

        var body = JsonSerializer.Serialize(new { leader, candidate });

        // Addressed to the LEADER. Patroni accepts /switchover on the member that is
        // giving up the role; sending it elsewhere is answered with a redirect or a
        // refusal depending on version, and neither is worth depending on.
        var target = _settings.Nodes.FirstOrDefault(n => n.Contains(leader, StringComparison.OrdinalIgnoreCase));
        foreach (var node in target is null ? _settings.Nodes : [target])
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                var response = await client.PostAsync($"{node.TrimEnd('/')}/switchover", content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (response.IsSuccessStatusCode) return (true, text);

                // 401 means the credential is wrong; 403 means this control plane is
                // not on the node's allowlist. Both are configuration, and both are
                // worth saying out loud rather than reporting as "switchover failed".
                return response.StatusCode switch
                {
                    System.Net.HttpStatusCode.Unauthorized =>
                        (false, "Patroni rejected the credentials (401). Check Patroni__Password against /etc/patroni/config.yml."),
                    System.Net.HttpStatusCode.Forbidden =>
                        (false, "Patroni refused this host (403). The control plane's address is missing from restapi.allowlist."),
                    _ => (false, $"Patroni answered {(int)response.StatusCode}: {text}"),
                };
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Switchover request to {Node} failed", node);
            }
        }
        return (false, "No Patroni node accepted the switchover request.");
    }
}
