using System.Security.Cryptography;
using ZuloOne.ControlPlane.Portal;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

/// <summary>
/// Sessions, verification links and reset links are all the same object: 32
/// random bytes whose SHA-256 is what gets stored. These check that the one
/// implementation they share actually has those properties.
/// </summary>
public sealed class PortalTokensTests
{
    [Fact]
    public void A_minted_token_hashes_to_the_stored_hash()
    {
        var (token, hash) = PortalTokens.Mint();

        Assert.Equal(hash, PortalTokens.HashOf(token));
    }

    /// <summary>
    /// 32 bytes, because this is the only secret between a stranger and somebody
    /// else's stand. A shorter one would still round-trip, still pass every other
    /// test here, and still be guessable.
    /// </summary>
    [Fact]
    public void A_minted_token_carries_full_entropy()
    {
        var (token, hash) = PortalTokens.Mint();

        Assert.Equal(32, PortalTokens.Decode(token).Length);
        Assert.Equal(SHA256.HashData(PortalTokens.Decode(token)), hash);
    }

    [Fact]
    public void Tokens_do_not_repeat()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 2000; i++)
            Assert.True(seen.Add(PortalTokens.Mint().Token));
    }

    /// <summary>
    /// The token travels in a URL query string and an Authorization header, so
    /// the characters that would need escaping in either must not appear.
    /// </summary>
    [Fact]
    public void A_token_is_url_and_header_safe()
    {
        for (var i = 0; i < 500; i++)
        {
            var (token, _) = PortalTokens.Mint();
            Assert.DoesNotContain(token, c => c is '+' or '/' or '=' or ' ');
            Assert.Equal(token, Uri.EscapeDataString(token));
        }
    }

    /// <summary>
    /// Anything that is not a token throws rather than hashing to something.
    /// Both callers catch this and answer "expired or already used", so the
    /// throw is what stops junk being looked up as if it were a credential.
    /// </summary>
    [Theory]
    [InlineData("not base64 at all !!")]
    [InlineData("тоже нет")]
    public void Garbage_does_not_silently_hash(string garbage)
    {
        Assert.ThrowsAny<Exception>(() => PortalTokens.HashOf(garbage));
    }

    /// <summary>
    /// The empty string decodes to zero bytes and hashes fine — so it would
    /// match a stored hash of the empty string, if one could ever exist. It
    /// cannot: Mint is the only writer. Recorded because the absence of a throw
    /// here reads as a gap otherwise.
    /// </summary>
    [Fact]
    public void The_empty_string_hashes_but_can_never_match_a_minted_token()
    {
        Assert.Equal(SHA256.HashData([]), PortalTokens.HashOf(string.Empty));

        for (var i = 0; i < 100; i++)
            Assert.NotEqual(PortalTokens.Mint().Hash, PortalTokens.HashOf(string.Empty));
    }
}
