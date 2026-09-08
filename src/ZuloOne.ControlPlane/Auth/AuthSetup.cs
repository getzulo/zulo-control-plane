using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
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
                // Cloudflare publishes proper OIDC discovery at
                // {TeamDomain}/.well-known/openid-configuration, whose jwks_uri
                // points at /cdn-cgi/access/certs. Setting Authority lets the
                // standard configuration manager do the fetching, and — the part
                // that matters — the refreshing.
                //
                // That is not a convenience. Cloudflare rotates these signing keys
                // every six weeks, honouring the previous one for seven days. Read
                // them once at startup and the panel works through every test, then
                // locks out every operator about a month and a half later, with no
                // code change and nothing in the logs that points at it. The manager
                // refreshes on a schedule and again on an unrecognised key id, and
                // throttles that second path so forged key ids cannot turn into a
                // fetch per request.
                options.Authority = access.TeamDomain;

                // Keep the token's OWN claim names. JwtBearer otherwise rewrites
                // short names into the long WS-Federation URIs — `email` becomes
                // http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress
                // — so a lookup by "email" silently finds nothing and every
                // identity is rejected as having no address. The token is valid,
                // the signature checks out, and the panel refuses everyone.
                options.MapInboundClaims = false;

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
                        // Both spellings: MapInboundClaims is off above, so `email`
                        // arrives as itself — but a future toggle, or a different
                        // handler, would deliver the mapped URI instead, and this
                        // failing silently costs an afternoon.
                        var email = context.Principal?.FindFirst("email")?.Value
                                    ?? context.Principal?.FindFirst(ClaimTypes.Email)?.Value;

                        if (string.IsNullOrWhiteSpace(email)
                            || !access.AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase))
                        {
                            // Also rejects Cloudflare SERVICE tokens, which carry
                            // common_name and no email. Correct by default; handle
                            // common_name explicitly if automation ever needs in.
                            context.Fail("Not an allowed operator.");

                            // Name the claims that DID arrive. "Rejected (none)"
                            // says the token had no address, which is
                            // indistinguishable from the address being read under
                            // the wrong name — and those need opposite fixes.
                            var claims = string.Join(", ",
                                context.Principal?.Claims.Select(c => c.Type) ?? []);
                            context.HttpContext.RequestServices
                                .GetRequiredService<ILoggerFactory>()
                                .CreateLogger("ControlPlane.Access")
                                .LogWarning(
                                    "Rejected Access identity {Email} — not in Access:AllowedEmails. Claims present: {Claims}",
                                    email ?? "(no email claim)", claims);
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
