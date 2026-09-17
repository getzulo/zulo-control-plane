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
                // Old Core 422'd the whole tree after MinVersion against a stale
                // AsNoTracking snapshot — Created/Updated in the body are real.
                // Parse them so the job can name the models instead of dumping JSON.
                var parsed = Parse(body, url);
                if (parsed.Created > 0 || parsed.Updated > 0 || parsed.Errors.Count > 0)
                    return parsed with { Succeeded = false };
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

    /// <summary>
    /// Makes already-installed metadata runnable: tables, generated entity types,
    /// then scripts. Does not write a model tree.
    /// </summary>
    public async Task<TenantInstallResult> MaterializeAsync(
        Tenant tenant, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.JwtSigningKey))
        {
            return new TenantInstallResult(false, 0, 0,
                ["The registry holds no signing key for this tenant, so the panel cannot authenticate to it."],
                [], null);
        }

        var host = _containers.HostFor(tenant.Slug);
        using var client = _http.CreateClient("tenant");
        client.Timeout = timeout;

        async Task<(int Status, string Body)> Post(string path)
        {
            var url = $"https://{host}{path}";
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(tenant));
            using var response = await client.SendAsync(request, ct);
            return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }

        try
        {
            var (schemaStatus, schemaBody) = await Post("/api/schema/sync");
            if (schemaStatus is < 200 or >= 300)
            {
                return new TenantInstallResult(false, 0, 0,
                    [$"{schemaStatus} schema/sync: {Trim(schemaBody)}"], [], $"https://{host}/api/schema/sync");
            }

            var (entityStatus, entityBody) = await Post("/api/metadata/compile");
            if (entityStatus is < 200 or >= 300)
            {
                return new TenantInstallResult(false, 0, 0,
                    [$"{entityStatus} metadata/compile: {Trim(entityBody)}"], [], $"https://{host}/api/metadata/compile");
            }

            var (scriptStatus, scriptBody) = await Post("/api/metadata/models/compile");
            if (scriptStatus is < 200 or >= 300)
            {
                return new TenantInstallResult(false, 0, 0,
                    [$"{scriptStatus} models/compile: {Trim(scriptBody)}"], [], $"https://{host}/api/metadata/models/compile");
            }

            return ParseCompile(scriptBody, $"https://{host}/api/metadata/models/compile");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Materializing models on {Slug} failed.", tenant.Slug);
            return new TenantInstallResult(false, 0, 0, [$"Could not reach {host}: {ex.Message}"], [], host);
        }
    }

    /// <summary>What the tenant reported back from a cascade delete.</summary>
    public sealed record TenantDeleteModelResult(
        bool Succeeded,
        int Status,
        IReadOnlyList<string> DependentModels,
        string? Error,
        int RowsDeleted,
        IReadOnlyList<string> DroppedTables);

    /// <summary>
    /// Cascade-deletes one model on a RUNNING tenant. The token is the same
    /// <c>control-plane</c> Administrator the install uses; Core opens
    /// <c>PlatformInstallScope</c> for that name so a Zulo product model can
    /// actually leave.
    /// </summary>
    public async Task<TenantDeleteModelResult> DeleteModelAsync(
        Tenant tenant, Guid modelId, TimeSpan timeout, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tenant.JwtSigningKey))
        {
            return new TenantDeleteModelResult(false, 0, [],
                "The registry holds no signing key for this tenant, so the panel cannot authenticate to it.",
                0, []);
        }

        var host = _containers.HostFor(tenant.Slug);
        var url = $"https://{host}/api/metadata/models/{modelId}";
        using var client = _http.CreateClient("tenant");
        client.Timeout = timeout;
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(tenant));

        try
        {
            using var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;

            if (status == 409)
            {
                var names = ParseDependentNames(body);
                return new TenantDeleteModelResult(false, status, names,
                    names.Count > 0
                        ? $"First remove {string.Join(", ", names)}."
                        : Trim(body),
                    0, []);
            }

            if (status is < 200 or >= 300)
            {
                return new TenantDeleteModelResult(false, status, [],
                    $"{status} from {host}: {Trim(body)}", 0, []);
            }

            return ParseDelete(body, status);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Deleting a model on {Slug} failed.", tenant.Slug);
            return new TenantDeleteModelResult(false, 0, [], $"Could not reach {host}: {ex.Message}", 0, []);
        }
    }

    private static TenantDeleteModelResult ParseDelete(string body, int status)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var tables = root.TryGetProperty("droppedTables", out var t) && t.ValueKind == JsonValueKind.Array
                ? t.EnumerateArray().Select(x => x.ToString()).ToList()
                : [];
            var rows = root.TryGetProperty("rowsDeleted", out var r) && r.TryGetInt32(out var n) ? n : 0;
            return new TenantDeleteModelResult(true, status, [], null, rows, tables);
        }
        catch (JsonException)
        {
            return new TenantDeleteModelResult(false, status, [], $"Unreadable delete answer: {Trim(body)}", 0, []);
        }
    }

    private static List<string> ParseDependentNames(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("dependentModels", out var list)
                && list.ValueKind == JsonValueKind.Array)
            {
                return list.EnumerateArray()
                    .Select(x => x.GetString())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Cast<string>()
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Fall through: the caller still has the raw body.
        }
        return [];
    }

    private static TenantInstallResult ParseCompile(string body, string url)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var models = root.TryGetProperty("models", out var list) && list.ValueKind == JsonValueKind.Array
                ? list
                : default;
            var problems = new List<string>();
            if (models.ValueKind == JsonValueKind.Array)
            {
                problems.AddRange(models.EnumerateArray()
                    .Where(m => m.TryGetProperty("status", out var s)
                             && !string.Equals(s.GetString(), "Ok", StringComparison.OrdinalIgnoreCase))
                    .Select(m => $"{Prop(m, "name")}: {Prop(m, "status")} {Prop(m, "error")}".Trim()));
            }

            return new TenantInstallResult(true, 0, 0, [], problems, url);
        }
        catch (JsonException)
        {
            return new TenantInstallResult(false, 0, 0, [$"Unreadable compile answer: {Trim(body)}"], [], url);
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
                    .Select(m => $"{Prop(m, "name")}: {Prop(m, "status")} {Prop(m, "error")}".Trim()));
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

    private static string Prop(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ToString() : string.Empty;

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300] + "…";
}
