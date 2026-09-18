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

    [Fact]
    public void ResolveDeleteRepository_hits_the_dist_repo_when_the_full_image_says_so()
        => Assert.Equal("zuloone", ImagesController.ResolveDeleteRepository(
            "zuloone-core", "2026.0.69", "10.10.0.210:5000/zuloone:2026.0.69"));

    [Fact]
    public void ResolveDeleteRepository_hits_the_platform_repo_when_the_full_image_says_so()
        => Assert.Equal("zuloone-core", ImagesController.ResolveDeleteRepository(
            "zuloone-core", "2026.0.69", "10.10.0.210:5000/zuloone-core:2026.0.69"));

    [Fact]
    public void ResolveDeleteRepository_rejects_a_tag_mismatch()
        => Assert.Null(ImagesController.ResolveDeleteRepository(
            "zuloone-core", "2026.0.69", "10.10.0.210:5000/zuloone:2026.0.50"));

    [Fact]
    public void ResolveDeleteRepository_rejects_a_foreign_repository()
        => Assert.Null(ImagesController.ResolveDeleteRepository(
            "zuloone-core", "2026.0.69", "10.10.0.210:5000/other:2026.0.69"));

    [Fact]
    public void DistKeepWindow_keeps_commit_siblings_of_the_newest_packs()
    {
        var digests = new Dictionary<string, string?>
        {
            ["2026.0.69"] = "d69",
            ["sha-aaa"] = "d69",
            ["2026.0.68"] = "d68",
            ["sha-bbb"] = "d68",
            ["2026.0.50"] = "d50",
            ["sha-ccc"] = "d50",
        };
        var keep = ImagesController.DistKeepWindow(digests.Keys.ToList(), digests, keep: 2);
        Assert.Contains("2026.0.69", keep);
        Assert.Contains("sha-aaa", keep);
        Assert.Contains("2026.0.68", keep);
        Assert.Contains("sha-bbb", keep);
        Assert.DoesNotContain("2026.0.50", keep);
        Assert.DoesNotContain("sha-ccc", keep);
    }

    [Fact]
    public void Test_fixtures_are_not_a_product_name()
    {
        Assert.True(TestFixtureModel.IsName("TestBench"));
        Assert.True(TestFixtureModel.IsName("TestBenchExt"));
        Assert.True(TestFixtureModel.IsMetaId(TestFixtureModel.TestBenchMetaId));
        Assert.False(TestFixtureModel.IsName("Inventory"));
    }

    [Fact]
    public void ParseModels_drops_test_fixtures_from_image_labels()
    {
        var parsed = RegistryModelCatalog.ParseModels("Inventory=1.9.1,TestBench=1.0.8,TestBenchExt=1.0.0,Sales=1.18.1");
        Assert.Equal(["Inventory", "Sales"], parsed.Select(m => m.Name).ToArray());
    }
}
