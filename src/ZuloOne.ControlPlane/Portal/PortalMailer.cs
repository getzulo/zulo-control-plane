using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using ZuloOne.ControlPlane.Provisioning;

namespace ZuloOne.ControlPlane.Portal;

/// <summary>
/// The two letters the portal sends: prove your address, and set a new password.
/// </summary>
/// <remarks>
/// Never throws, like <see cref="TenantInviteService"/> — but for a sharper
/// reason. Both callers must answer identically whether or not the address exists,
/// so an exception escaping here would turn a 500 into the account-enumeration
/// oracle the callers carefully avoid being.
/// </remarks>
public sealed class PortalMailer
{
    private readonly ControlPlaneMailSettings _mail;
    private readonly PortalSettings _portal;
    private readonly ILogger<PortalMailer> _logger;

    public PortalMailer(
        IOptions<ControlPlaneMailSettings> mail,
        IOptions<PortalSettings> portal,
        ILogger<PortalMailer> logger)
    {
        _mail = mail.Value;
        _portal = portal.Value;
        _logger = logger;
    }

    public Task SendVerificationAsync(string email, string? displayName, string? locale, string token, CancellationToken ct = default)
    {
        var link = Link(_portal.VerifyPath, locale, token);
        return SendAsync(email,
            "Confirm your address",
            $"""
             <p>Hello{(string.IsNullOrWhiteSpace(displayName) ? "" : " " + Escape(displayName))},</p>
             <p>Confirm this address to finish setting up your ZuloOne account:</p>
             <p><a href="{link}">{link}</a></p>
             <p>The link is good for {_portal.VerifyTokenHours} hours. If you did not ask for
             an account, nothing has been created in your name and you can ignore this.</p>
             """, ct);
    }

    public Task SendPasswordResetAsync(string email, string? locale, string token, CancellationToken ct = default)
    {
        var link = Link(_portal.ResetPath, locale, token);
        return SendAsync(email,
            "Set a new password",
            $"""
             <p>Someone asked to reset the password for this address.</p>
             <p><a href="{link}">{link}</a></p>
             <p>The link is good for {_portal.ResetTokenMinutes} minutes and can be used once.
             If it was not you, your password has not changed and there is nothing to do.</p>
             """, ct);
    }

    /// <summary>
    /// Builds a link from the CONFIGURED public address, never from the request.
    /// </summary>
    /// <remarks>
    /// A link built from an inbound Host header is host-header injection: the mail
    /// reaches the right mailbox with a link to somebody else's server, and the
    /// person who owns the address hands over their own token. Configuration is
    /// the only source here that an attacker cannot set.
    /// </remarks>
    private string Link(string path, string? locale, string token)
    {
        var root = (_portal.PublicUrl ?? string.Empty).TrimEnd('/');

        // The locale reaches this string from the account row, which the person
        // signing up filled in. It is therefore checked against an allow-list
        // rather than escaped: escaping would keep the link on our host but
        // still let a stranger choose the path it lands on, and this link
        // carries a live token.
        var lang = locale ?? string.Empty;
        if (!_portal.Locales.Contains(lang, StringComparer.OrdinalIgnoreCase))
        {
            lang = _portal.DefaultLocale;
        }

        var resolved = path.Replace("{locale}", lang, StringComparison.OrdinalIgnoreCase);
        return $"{root}{resolved}?token={Uri.EscapeDataString(token)}";
    }

    private async Task SendAsync(string to, string subject, string html, CancellationToken ct)
    {
        if (!_mail.Enabled || string.IsNullOrWhiteSpace(_mail.Host)
            || string.IsNullOrWhiteSpace(_mail.FromAddress))
        {
            // Loud, because the caller cannot be: it answers "check your mail"
            // either way, so this line is the only trace that nothing was sent.
            _logger.LogWarning(
                "Portal mail '{Subject}' for {To} was NOT sent — no mail is configured (Mail:Enabled, Mail:Host, Mail:FromAddress)",
                subject, to);
            return;
        }

        if (string.IsNullOrWhiteSpace(_portal.PublicUrl))
        {
            // A relative link in an e-mail client goes nowhere. Refusing to send is
            // better than sending a letter whose only purpose is a link that cannot
            // be clicked.
            _logger.LogError(
                "Portal mail '{Subject}' for {To} was NOT sent — Portal:PublicUrl is empty, so the link would have no host",
                subject, to);
            return;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_mail.FromName ?? "ZuloOne", _mail.FromAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;
            message.Body = new BodyBuilder { HtmlBody = html }.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(_mail.Host, _mail.Port,
                _mail.UseSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable, ct);
            if (!string.IsNullOrWhiteSpace(_mail.UserName))
                await client.AuthenticateAsync(_mail.UserName, _mail.Password ?? string.Empty, ct);
            await client.SendAsync(message, ct);
            await client.DisconnectAsync(true, ct);

            // The address, never the token. Logs are aggregated, shipped and backed
            // up, and a reset link in a log stream is a password in a log stream.
            _logger.LogInformation("Portal mail '{Subject}' sent to {To}", subject, to);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Portal mail '{Subject}' to {To} failed", subject, to);
        }
    }

    /// <summary>
    /// The display name goes into HTML we compose, and it came from a registration
    /// form. Everything else in these letters is ours.
    /// </summary>
    private static string Escape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
