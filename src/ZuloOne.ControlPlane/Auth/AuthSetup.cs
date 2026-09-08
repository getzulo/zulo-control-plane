using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ZuloOne.ControlPlane.Auth;

public static class AuthSetup
{
    public const string AccessScheme = "CloudflareAccess";

    public static IServiceCollection AddControlPlaneAuth(
        this IServiceCollection services, IConfiguration configuration, ILogger logger)
    {
        var access = configuration.GetSection("Access").Get<AccessSettings>() ?? new AccessSettings();
        var op = configuration.GetSection("Operator").Get<OperatorSettings>() ?? new OperatorSettings();
        services.Configure<AccessSettings>(configuration.GetSection("Access"));
        services.Configure<OperatorSettings>(configuration.GetSection("Operator"));

        // No default scheme. With one set, HttpContext.User is populated from it on
        // every request whether or not the policy asked for it, which quietly makes
        // "authenticated" mean something different from "allowed here".
        var auth = services.AddAuthentication();

        auth.AddScheme<AuthenticationSchemeOptions, OperatorSessionHandler>(
            OperatorSessionHandler.SchemeName, _ => { });

        if (access.IsConfigured)
        {
            auth.AddJwtBearer(AccessScheme, options =>
            {
                var certs = $"{access.TeamDomain!.TrimEnd('/')}/cdn-cgi/access/certs";

                // ConfigurationManager, not a one-time fetch: Cloudflare rotates
                // these keys every six weeks. See CloudflareAccessKeys for why a
                // snapshot fails silently, weeks later, at the worst moment.
                options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                    certs, new CloudflareAccessKeys(), new HttpDocumentRetriever());

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = access.TeamDomain,
                    ValidAudience = access.Aud,
                    // Pinned. An open algorithm list is how signature-confusion
                    // attacks begin, and Access only ever signs with RS256.
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                };

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // The HEADER, never the CF_Authorization cookie. Two
                        // reasons: Cloudflare does not guarantee the cookie is
                        // passed to the origin, and a cookie is sent automatically
                        // by the browser — which would make every state-changing
                        // endpoint on this same-origin API forgeable from another
                        // site. Header-only closes both.
                        context.Token = context.Request.Headers["Cf-Access-Jwt-Assertion"];
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        var email = context.Principal?.FindFirst("email")?.Value;
                        if (string.IsNullOrWhiteSpace(email)
                            || !access.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
                        {
                            // Also rejects Cloudflare SERVICE tokens, which carry
                            // common_name and no email. Correct by default; handle
                            // common_name explicitly if automation ever needs in.
                            context.Fail("Not an allowed operator.");
                            context.HttpContext.RequestServices
                                .GetRequiredService<ILoggerFactory>()
                                .CreateLogger("ControlPlane.Access")
                                .LogWarning("Rejected Access identity {Email} — not in Access:AllowedEmails", email ?? "(none)");
                            return Task.CompletedTask;
                        }

                        var identity = (ClaimsIdentity)context.Principal!.Identity!;
                        identity.AddClaim(new Claim("zuloone.cp.mode", "access"));
                        return Task.CompletedTask;
                    },
                };
            });
        }
        else
        {
            // Refusing to register is the point. A JWT scheme with ValidateAudience
            // against a null audience, or an empty allowlist, is not "not
            // configured yet" — it is an authentication bypass wearing the shape of
            // one that works.
            logger.LogWarning(
                "Cloudflare Access is NOT configured (needs Access:TeamDomain, Access:Aud and a non-empty Access:AllowedEmails) — the panel is reachable only through the break-glass path");
        }

        if (!op.IsConfigured)
            logger.LogWarning(
                "No break-glass operator configured (Operator:Email + Operator:PasswordHash) — if Cloudflare Access is unavailable there will be no way in");

        // BOTH policies, not just the fallback. FallbackPolicy applies only to
        // endpoints carrying no authorization metadata; the moment anything is
        // marked [Authorize] the DefaultPolicy applies instead, and the stock
        // default names no schemes — so it would accept only one of the two ways in,
        // silently, and only on the endpoints someone had bothered to annotate.
        var schemes = access.IsConfigured
            ? new[] { AccessScheme, OperatorSessionHandler.SchemeName }
            : [OperatorSessionHandler.SchemeName];

        var either = new AuthorizationPolicyBuilder(schemes)
            .RequireAuthenticatedUser()
            .Build();

        services.AddAuthorizationBuilder()
            .SetDefaultPolicy(either)
            .SetFallbackPolicy(either);

        return services;
    }
}
