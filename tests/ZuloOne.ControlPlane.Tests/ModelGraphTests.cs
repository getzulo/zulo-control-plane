using ZuloOne.ControlPlane.Api;
using ZuloOne.ControlPlane.Provisioning;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class ModelGraphTests
{
    private const string CommonJson = """
        {
          "object": { "name": "Common", "modelVersion": "1.0.0", "isSystem": false, "metaId": "aa" },
          "dependencies": [
            { "dependsOnModelMetaId": "core", "name": "Common->Core" }
          ]
        }
        """;

    private const string CoreJson = """
        {
          "object": { "name": "Core", "modelVersion": "2026.0.80", "isSystem": true, "metaId": "core" },
          "dependencies": []
        }
        """;

    private const string PurchasingJson = """
        {
          "object": { "name": "Purchasing", "modelVersion": "1.2.0", "isSystem": false, "metaId": "pu" },
          "dependencies": [
            { "dependsOnModelMetaId": "aa", "name": "Purchasing->Common" },
            { "dependsOnModelMetaId": "inv", "name": "Purchasing->Inventory" }
          ]
        }
        """;

    private const string InventoryJson = """
        {
          "object": { "name": "Inventory", "modelVersion": "1.3.0", "isSystem": false, "metaId": "inv" },
          "dependencies": [
            { "dependsOnModelMetaId": "aa", "name": "Inventory->Common" }
          ]
        }
        """;

    [Fact]
    public void Parse_resolves_dependencies_by_meta_id_and_arrow_name()
    {
        var graph = ModelGraph.Parse([
            ("Common/model.json", CommonJson),
            ("Core/model.json", CoreJson),
            ("Purchasing/model.json", PurchasingJson),
            ("Inventory/model.json", InventoryJson),
        ]).ToDictionary(n => n.Name);

        Assert.Equal(["Core"], graph["Common"].DependsOn);
        Assert.Equal(["Common"], graph["Inventory"].DependsOn);
        Assert.Contains("Common", graph["Purchasing"].DependsOn);
        Assert.Contains("Inventory", graph["Purchasing"].DependsOn);
        Assert.True(graph["Core"].IsSystem);
    }

    [Fact]
    public void Expand_pulls_transitive_dependencies()
    {
        var graph = ModelGraph.Parse([
            ("Common/model.json", CommonJson),
            ("Core/model.json", CoreJson),
            ("Purchasing/model.json", PurchasingJson),
            ("Inventory/model.json", InventoryJson),
        ]).ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);

        var expanded = ModelGraph.Expand(["Purchasing"], graph);

        Assert.Contains("Purchasing", expanded);
        Assert.Contains("Inventory", expanded);
        Assert.Contains("Common", expanded);
        Assert.Contains("Core", expanded);
    }

    [Fact]
    public void Outdated_is_a_real_version_compare()
    {
        Assert.True(ModelGraph.IsOutdated("1.0.0", "1.3.0"));
        Assert.False(ModelGraph.IsOutdated("1.3.0", "1.3.0"));
        Assert.False(ModelGraph.IsOutdated("1.3.0", "1.2.0"));
        Assert.False(ModelGraph.IsOutdated("1.0.0", null));
    }

    [Fact]
    public void Compile_ok_accepts_what_core_actually_writes()
    {
        Assert.True(ModelGraph.CompilesOk("Ok"));
        Assert.True(ModelGraph.CompilesOk("Success"));
        Assert.True(ModelGraph.CompilesOk(null));
        Assert.False(ModelGraph.CompilesOk("Failed"));
    }

    [Fact]
    public void PickDistribution_skips_platform_only_core()
    {
        var images = new List<CatalogueImage>
        {
            new("zuloone-core", "2026.9.18", "reg/zuloone-core:2026.9.18", null, null, null, []),
            new("zuloone", "2026.9.10", "reg/zuloone:2026.9.10", null, null, null,
                [new CatalogueModel("Common", "1.0.0")]),
            new("zuloone", "2026.9.12", "reg/zuloone:2026.9.12", null, null, null,
                [new CatalogueModel("Common", "1.0.0"), new CatalogueModel("Purchasing", "1.2.0")]),
        };

        Assert.Equal("reg/zuloone:2026.9.12", ModelsController.PickDistribution(images));
    }
}
