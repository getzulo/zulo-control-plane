using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Registry;

namespace ZuloOne.ControlPlane.Auth;

/// <summary>
/// Brings the single break-glass account into line with configuration at startup.
/// </summary>
public static class OperatorSeeder
{
    public static async Task SeedAsync(IServiceProvider services, ILogger logger, CancellationToken ct = default)
    {
        var settings = services.GetRequiredService<IOptions<OperatorSettings>>().Value;
        if (!settings.IsConfigured)
        {
            logger.LogWarning("No break-glass operator in configuration — skipping seed");
            return;
        }

        var db = services.GetRequiredService<ControlPlaneDbContext>();
        var account = await db.OperatorAccounts.FirstOrDefaultAsync(a => a.Email == settings.Email, ct);

        if (account is null)
        {
            db.OperatorAccounts.Add(new OperatorAccount
            {
                Email = settings.Email!,
                PasswordHash = settings.PasswordHash!,
            });
            await db.SaveChangesAsync(ct);
            logger.LogInformation(
                "Created break-glass operator {Email}. It cannot sign in until a second factor is enrolled.",
                settings.Email);
            return;
        }

        if (account.PasswordHash == settings.PasswordHash) return;

        // Changing the hash in configuration IS the password-change mechanism, and
        // it must invalidate outstanding sessions — otherwise rotating a password
        // because it may have leaked leaves the token minted with it still working.
        account.PasswordHash = settings.PasswordHash!;
        account.FailedAttempts = 0;
        account.LockedUntil = null;
        account.UpdatedAt = DateTime.UtcNow;

        var sessions = await db.OperatorSessions.Where(s => s.OperatorAccountId == account.Id).ToListAsync(ct);
        db.OperatorSessions.RemoveRange(sessions);
        await db.SaveChangesAsync(ct);

        // TotpSecret is deliberately untouched: configuration can rotate the
        // password, never the second factor.
        logger.LogInformation(
            "Break-glass password changed for {Email}; {Count} session(s) revoked", account.Email, sessions.Count);
    }
}
