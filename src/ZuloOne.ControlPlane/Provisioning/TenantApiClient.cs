using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>What the tenant reported back from an install.</summary>
public sealed record TenantInstallResult(
    bool Succeeded,
    int Created,
    int Updated,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> CompilationProblems,
    string? Transport);

/// <summary>
/// Talks to a tenant's own API, as an administrator of that tenant.
/// </summary>
/// <remarks>
/// <para>
/// The panel reaches tenants over HTTPS through their public hostname — the same path
/// the health probe has always used. "The control plane cannot call in" was only ever
/// true of the Docker socket, where <c>EXEC=0</c> forbids running commands inside a
/// container; HTTP was never closed and this makes that explicit.
/// </para>
///
/// <para>
/// <b>The panel mints its own token.</b> It already stores each tenant's
/// <see cref="Tenant.JwtSigningKey"/> — it generated it — so it can sign one the
/// tenant will accept. That turns a stored secret into an exercised capability, which
/// is a real widening of what a compromised panel is worth, and it is the price of
/// installing without recreating the container. The token is minted per call, lives
/// minutes, and names the operation it was minted for.
/// </para>
///
/// <para>
/// Issuer and audience are the platform's compiled-in values; the role is
/// <c>Administrator</c>, which every tenant seeds and which short-circuits the
/// permission check as a superuser.
/// </para>
/// </remarks>
public sealed class TenantApiClient
{
    // Fixed in the platform's appsettings; a tenant validates both.
    private const string Issuer = "ZuloOneCore";
    private const string Audience = "ZuloOneSpa";
    private const string SuperuserRole = "Administrator";

    private readonly IHttpClientFactory _http;
    private readonly TenantContainerService _containers;
    private readonly ILogger<TenantApiClient> _logger;

    public TenantApiClient(
        IHttpClientFactory http, TenantContainerService containers, ILogger<TenantApiClient> logger)
    {
        _http = http;
        _containers = containers;
        _logger = logger;
    }

    /// <summary>
    /// Pushes a model tree into a RUNNING tenant and waits for it to install and compile.
    /// </summary>
    /// <remarks>
    /// No container is touched. The caller owns the rollback: there is no previous
    /// image to pin back here, so the snapshot taken beforehand is the only way out.
    /// </remarks>
    public async Task<TenantInstallResult> InstallTreeAsync(
        Tenant tenant, byte[] treeTarGz, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.JwtSigningKey))
        {
            return new TenantInstallResult(false, 0, 0,
                ["The registry holds no signing key for this tenant, so the panel cannot authenticate to it."],
                [], null);
        }

        var host = _containers.HostFor(tenant.Slug);
        var url = $"https://{host}/api/metadata/models/install-tree";

        using var client = _http.CreateClient("tenant");
        client.Timeout = timeout;

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(treeTarGz);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/gzip");
        content.Add(file, "archive", "workspace.tar.gz");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(tenant));

        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                return new TenantInstallResult(false, 0, 0,
                    [$"{(int)response.StatusCode} from {host}: {Trim(body)}"], [], url);
            }

            return Parse(body, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Pushing the model tree to {Slug} failed.", tenant.Slug);
            return new TenantInstallResult(false, 0, 0, [$"Could not reach {host}: {ex.Message}"], [], url);
        }
    }

    private static TenantInstallResult Parse(string body, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var errors = root.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Select(x => x.ToString()).ToList()
                : [];

            // A model that installed and does not build is not a success the caller
            // should act on quietly — surfaced separately so a wave can stop on it.
            var problems = new List<string>();
            if (root.TryGetProperty("compiled", out var compiled) && compiled.ValueKind == JsonValueKind.Array)
            {
                problems.AddRange(compiled.EnumerateArray()
                    .Where(m => m.TryGetProperty("status", out var s)
                             && !string.Equals(s.GetString(), "Ok", StringComparison.OrdinalIgnoreCase))
                    .Select(m => $"{Get(m, "name")}: {Get(m, "status")} {Get(m, "error")}".Trim()));
            }

            return new TenantInstallResult(
                root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True,
                root.TryGetProperty("created", out var c) && c.TryGetInt32(out var created) ? created : 0,
                root.TryGetProperty("updated", out var u) && u.TryGetInt32(out var updated) ? updated : 0,
                errors, problems, url);
        }
        catch (JsonException)
        {
            return new TenantInstallResult(false, 0, 0, [$"Unreadable answer from the tenant: {Trim(body)}"], [], url);
        }

        static string Get(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;
    }

    private static string MintToken(Tenant tenant)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(tenant.JwtSigningKey!));
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                // Names the panel, not a person: an audit trail that reads
                // "control-plane" is the truth, and inventing a user would not be.
                new Claim(ClaimTypes.Name, "control-plane"),
                new Claim(ClaimTypes.NameIdentifier, $"control-plane:{tenant.Slug}"),
                new Claim(ClaimTypes.Role, SuperuserRole),
            ],
            // Minutes, because an install can take some: long enough to finish, short
            // enough that a leaked token is not a standing key to the tenant.
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
