using ZuloOne.ControlPlane.Api;
using ZuloOne.ControlPlane.Provisioning;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class ImagePinTests
{
    const string Pin = "10.10.0.210:5000/zuloone-core:2026.9.28";

    [Fact]
    public void Same_tag_matches_even_when_health_version_would_not()
        => Assert.True(ImagePin.Matches(Pin, Pin, runningId: null, pinnedId: null));

    [Fact]
    public void Tag_compare_is_case_insensitive()
        => Assert.True(ImagePin.Matches(Pin, Pin.ToUpperInvariant(), null, null));

    [Fact]
    public void Different_tags_on_the_same_digest_match()
        => Assert.True(ImagePin.Matches(
            Pin,
            "10.10.0.210:5000/zuloone-core:2026.9.6",
            runningId: "sha256:abc",
            pinnedId: "sha256:abc"));

    [Fact]
    public void Digest_compare_strips_the_sha256_prefix_on_one_side()
        => Assert.True(ImagePin.Matches(
            Pin,
            "10.10.0.210:5000/zuloone-core:2026.0.40",
            runningId: "abc",
            pinnedId: "sha256:abc"));

    [Fact]
    public void Different_tags_and_digests_do_not_match()
        => Assert.False(ImagePin.Matches(
            Pin,
            "10.10.0.210:5000/zuloone-core:2026.9.6",
            runningId: "sha256:aaa",
            pinnedId: "sha256:bbb"));

    [Fact]
    public void Different_tags_without_digests_do_not_match()
        => Assert.False(ImagePin.Matches(Pin, "10.10.0.210:5000/zuloone-core:2026.9.6", null, null));

    [Fact]
    public void Empty_pin_never_matches()
        => Assert.False(ImagePin.Matches("", Pin, "sha256:abc", "sha256:abc"));

    [Fact]
    public void Distribution_repository_strips_the_core_suffix()
        => Assert.Equal("zuloone", ImagesController.DistributionRepository("zuloone-core"));

    [Fact]
    public void Distribution_repository_is_null_when_the_fleet_is_not_a_core_pin()
        => Assert.Null(ImagesController.DistributionRepository("zuloone"));
}
