using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Portal;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

/// <summary>
/// The portal's whole security argument is that a customer credential cannot
/// authenticate an operator endpoint, and vice versa. That rests entirely on
/// which schemes each authorization policy names — which is not visible at any
/// call site and has no compile-time consequence when it is wrong.
/// </summary>
/// <remarks>
/// <para>
/// It WAS wrong first time round, which is why these exist.
/// <c>[Authorize(AuthenticationSchemes = "PortalSession")]</c> looks like it pins
/// the scheme list. It does not: with no policy named,
/// <c>AuthorizationPolicy.CombineAsync</c> still folds in the default policy, and
/// <c>Combine</c> takes the UNION of the scheme lists — so the portal's endpoints
/// quietly also accepted CloudflareAccess and OperatorSession, under a comment
/// asserting the opposite.
/// </para>
/// <para>
/// Nothing about that would have failed a build, a smoke test or a manual click
/// through the portal. These assertions are the only thing that would.
/// </para>
/// </remarks>
public sealed class PortalAuthPolicyTests
{
    private static ServiceProvider Build(bool withCloudflareAccess)
    {
        var settings = new Dictionary<string, string?>();
        if (withCloudflareAccess)
        {
            settings["Access:TeamDomain"] = "https://example.cloudflareaccess.com";
            settings["Access:Aud"] = "aud-value";
            settings["Access:AllowedEmails:0"] = "operator@example.com";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControlPlaneAuth(configuration, NullLogger.Instance);
        return services.BuildServiceProvider();
    }

    private static async Task<AuthorizationPolicy> PolicyAsync(ServiceProvider provider, string name)
    {
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync(name);
        Assert.NotNull(policy);
        return policy!;
    }

    /// <summary>
    /// The portal policy names the portal scheme and nothing else. An operator
    /// credential presented to /api/portal must not be accepted.
    /// </summary>
    [Fact]
    public async Task Portal_policy_names_only_the_portal_scheme()
    {
        using var provider = Build(withCloudflareAccess: true);
        var policy = await PolicyAsync(provider, AuthSetup.PortalPolicy);

        Assert.Equal([PortalSessionHandler.SchemeName], policy.AuthenticationSchemes);
    }

    /// <summary>
    /// The direction that actually protects the fleet: a customer token must not
    /// authenticate <c>/api/tenants</c>, which can delete databases.
    /// </summary>
    [Fact]
    public async Task Operator_policies_never_name_the_portal_scheme()
    {
        using var provider = Build(withCloudflareAccess: true);
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var @default = await policyProvider.GetDefaultPolicyAsync();
        var fallback = await policyProvider.GetFallbackPolicyAsync();

        Assert.DoesNotContain(PortalSessionHandler.SchemeName, @default.AuthenticationSchemes);
        Assert.NotNull(fallback);
        Assert.DoesNotContain(PortalSessionHandler.SchemeName, fallback!.AuthenticationSchemes);
    }

    /// <summary>
    /// Still true with Cloudflare Access unconfigured — the break-glass-only
    /// shape, which is how a developer machine and an Access outage both run.
    /// Isolation that only holds in the fully configured case is not isolation.
    /// </summary>
    [Fact]
    public async Task Operator_policies_never_name_the_portal_scheme_without_access()
    {
        using var provider = Build(withCloudflareAccess: false);
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var @default = await policyProvider.GetDefaultPolicyAsync();
        Assert.Equal([OperatorSessionHandler.SchemeName], @default.AuthenticationSchemes);

        var portal = await PolicyAsync(provider, AuthSetup.PortalPolicy);
        Assert.Equal([PortalSessionHandler.SchemeName], portal.AuthenticationSchemes);
    }

    /// <summary>
    /// The regression itself, stated as a test: this is what the attribute form
    /// produces, and it is why the code uses a named policy instead.
    /// </summary>
    /// <remarks>
    /// Asserting the BROKEN behaviour on purpose. If a future version of ASP.NET
    /// stops unioning scheme lists, this fails — and the failure is the signal
    /// that the named-policy workaround can be revisited, rather than a mystery.
    /// </remarks>
    [Fact]
    public async Task Directory_oidc_replaces_access_on_the_operator_policy()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Directory:Issuer"] = "https://login.example.com",
            ["Directory:ClientId"] = "controlplane",
            ["Directory:ClientSecret"] = "secret",
            ["Access:TeamDomain"] = "https://example.cloudflareaccess.com",
            ["Access:Aud"] = "aud-value",
            ["Access:AllowedEmails:0"] = "operator@example.com",
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddControlPlaneAuth(configuration, NullLogger.Instance);
        using var provider = services.BuildServiceProvider();
        var policy = await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetDefaultPolicyAsync();

        Assert.Contains(OperatorSessionHandler.SchemeName, policy.AuthenticationSchemes);
        Assert.Contains(DirectorySettings.CookieScheme, policy.AuthenticationSchemes);
        Assert.DoesNotContain(AuthSetup.AccessScheme, policy.AuthenticationSchemes);
        Assert.DoesNotContain(PortalSessionHandler.SchemeName, policy.AuthenticationSchemes);
    }

    /// <summary>
    /// The regression itself, stated as a test: this is what the attribute form
    /// produces, and it is why the code uses a named policy instead.
    /// </summary>
    /// <remarks>
    /// Asserting the BROKEN behaviour on purpose. If a future version of ASP.NET
    /// stops unioning scheme lists, this fails — and the failure is the signal
    /// that the named-policy workaround can be revisited, rather than a mystery.
    /// </remarks>
    [Fact]
    public async Task Naming_only_schemes_would_have_leaked_the_operator_schemes()
    {
        using var provider = Build(withCloudflareAccess: true);
        var policyProvider = provider.GetRequiredService<IAuthorizationPolicyProvider>();

        var combined = await AuthorizationPolicy.CombineAsync(
            policyProvider,
            [new AuthorizeAttribute { AuthenticationSchemes = PortalSessionHandler.SchemeName }]);

        Assert.NotNull(combined);
        Assert.Contains(PortalSessionHandler.SchemeName, combined!.AuthenticationSchemes);
        Assert.Contains(AuthSetup.AccessScheme, combined.AuthenticationSchemes);
        Assert.Contains(OperatorSessionHandler.SchemeName, combined.AuthenticationSchemes);
    }
}
