using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace ZuloOne.ControlPlane.Auth;

/// <summary>
/// Fetches Cloudflare Access's signing keys.
///
/// <para>
/// Cloudflare publishes a bare JWKS at <c>/cdn-cgi/access/certs</c> and does NOT
/// publish OpenID Connect discovery metadata, so neither <c>Authority</c> nor
/// <c>MetadataAddress</c> can be pointed at it — both expect
/// <c>.well-known/openid-configuration</c> and fail to parse a raw key set. This
/// adapter is the small piece that lets the standard
/// <see cref="ConfigurationManager{T}"/> consume it anyway.
/// </para>
///
/// <para>
/// Using the manager rather than reading the keys once at startup is not a detail.
/// Cloudflare rotates these keys every six weeks, with the previous key honoured
/// for seven days. A snapshot works perfectly through testing and then locks every
/// operator out of the panel about six weeks after deployment, with no code change
/// and nothing in the logs to connect it to. The manager refreshes on a schedule
/// AND on an unrecognised key id, and throttles that second path so a stream of
/// forged key ids cannot turn into a fetch per request.
/// </para>
/// </summary>
public sealed class CloudflareAccessKeys : IConfigurationRetriever<OpenIdConnectConfiguration>
{
    public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
        string address, IDocumentRetriever retriever, CancellationToken cancel)
    {
        var json = await retriever.GetDocumentAsync(address, cancel);
        var configuration = new OpenIdConnectConfiguration();
        foreach (var key in new JsonWebKeySet(json).GetSigningKeys())
            configuration.SigningKeys.Add(key);
        return configuration;
    }
}
