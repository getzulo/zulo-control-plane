using System.Net.Http.Json;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// Seeds a new tenant's administrator and invites them by e-mail
/// (ControlPlane.Deployment.md §6 step 6, §15 decision 4).
///
/// The generated password is e-mailed and never stored: the registry keeps who
/// the administrator IS, not how to log in as them.
/// </summary>
public sealed class TenantInviteService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ControlPlaneMailSettings _mail;
    private readonly ILogger<TenantInviteService> _logger;

    public TenantInviteService(
        IHttpClientFactory httpClientFactory,
        IOptions<ControlPlaneMailSettings> mail,
        ILogger<TenantInviteService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _mail = mail.Value;
        _logger = logger;
    }

    /// <summary>
    /// Creates the tenant's first user through the platform's one-shot
    /// <c>POST /api/auth/setup</c> — anonymous, and only while the user table is
    /// empty, so it can never be replayed against a live tenant. Returns the
    /// password to send on.
    /// </summary>
    public async Task<string> SeedAdministratorAsync(string host, string adminEmail, CancellationToken ct = default)
    {
        var password = GeneratePassword();
        using var client = _httpClientFactory.CreateClient("tenant");

        using var response = await client.PostAsJsonAsync($"http://{host}/api/auth/setup", new
        {
            name = "admin",
            email = adminEmail,
            password,
        }, ct);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Seeding the administrator failed ({(int)response.StatusCode}): {await response.Content.ReadAsStringAsync(ct)}");
        }

        _logger.LogInformation("Seeded administrator for {Host}", host);
        return password;
    }

    /// <summary>
    /// Sends the invitation. A tenant whose invitation could not be delivered is
    /// still a working tenant, so this never throws — it reports, and the operator
    /// can resend from the dashboard.
    /// </summary>
    public async Task SendInviteAsync(Tenant tenant, string host, string password, CancellationToken ct = default)
    {
        if (!_mail.Enabled || string.IsNullOrWhiteSpace(_mail.Host)
            || string.IsNullOrWhiteSpace(_mail.FromAddress) || string.IsNullOrWhiteSpace(tenant.AdminEmail))
        {
            _logger.LogWarning(
                "No mail configured — administrator for {Slug} was created but NOT invited. Password is not stored; reset it from the tenant.",
                tenant.Slug);
            return;
        }

        var url = $"https://{host}";
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_mail.FromName ?? "ZuloOne", _mail.FromAddress));
        message.To.Add(MailboxAddress.Parse(tenant.AdminEmail));
        message.Subject = $"Your ZuloOne workspace {tenant.Slug} is ready";
        message.Body = new BodyBuilder
        {
            HtmlBody = $"""
                <p>Your ZuloOne workspace is ready at <a href="{url}">{url}</a>.</p>
                <p>Sign in as <b>admin</b> with this one-time password:</p>
                <p style="font-size:18px"><code>{password}</code></p>
                <p>Change it right after your first sign-in — this message is the only copy.</p>
                """,
        }.ToMessageBody();

        try
        {
            using var client = new SmtpClient();
            await client.ConnectAsync(_mail.Host, _mail.Port,
                _mail.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            if (!string.IsNullOrWhiteSpace(_mail.UserName))
                await client.AuthenticateAsync(_mail.UserName, _mail.Password ?? string.Empty, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            _logger.LogInformation("Invited {Email} to tenant {Slug}", tenant.AdminEmail, tenant.Slug);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not invite {Email} to tenant {Slug} — the tenant is up regardless",
                tenant.AdminEmail, tenant.Slug);
        }
    }

    /// <summary>Readable enough to retype from an e-mail, random enough to be a password.</summary>
    private static string GeneratePassword()
        => Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(18))
            .Replace("+", "-").Replace("/", "_").TrimEnd('=') + "!1";
}
