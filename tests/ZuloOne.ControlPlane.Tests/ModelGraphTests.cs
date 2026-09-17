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
    public void DirectDependents_is_one_hop_not_the_cone()
    {
        var graph = ModelGraph.Parse([
            ("Common/model.json", CommonJson),
            ("Core/model.json", CoreJson),
            ("Purchasing/model.json", PurchasingJson),
            ("Inventory/model.json", InventoryJson),
        ]).ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);

        var onCommon = ModelGraph.DirectDependents("Common", graph);
        Assert.Contains("Inventory", onCommon);
        Assert.Contains("Purchasing", onCommon);
        Assert.DoesNotContain("Common", onCommon);

        var onInventory = ModelGraph.DirectDependents("Inventory", graph);
        Assert.Equal(["Purchasing"], onInventory);

        Assert.Empty(ModelGraph.DirectDependents("Purchasing", graph));
    }

    [Fact]
    public void Parse_records_extension_targets_as_extends_not_depends()
    {
        var accounting = """
            {
              "object": { "name": "Accounting", "modelVersion": "1.0.0", "isSystem": false, "metaId": "acc" },
              "dependencies": [
                { "dependsOnModelMetaId": "aa", "name": "Accounting->Common" }
              ]
            }
            """;
        var production = """
            {
              "object": { "name": "Production", "modelVersion": "1.0.0", "isSystem": false, "metaId": "prod" },
              "dependencies": []
            }
            """;
        var sales = """
            {
              "object": { "name": "Sales", "modelVersion": "1.0.0", "isSystem": false, "metaId": "sales" },
              "dependencies": []
            }
            """;
        var bom = """
            {
              "kind": "Dictionary",
              "object": { "name": "BillOfMaterials", "metaId": "bom", "modelId": "prod" }
            }
            """;
        var invoice = """
            {
              "kind": "Document",
              "object": { "name": "SalesInvoice", "metaId": "invc", "modelId": "sales" }
            }
            """;
        var bomExt = """
            {
              "kind": "DictionaryExtension",
              "object": { "name": "BillOfMaterials.Accounting", "modelId": "acc", "targetDictionaryMetaId": "bom" }
            }
            """;
        var invoiceExt = """
            {
              "kind": "DocumentExtension",
              "object": { "name": "SalesInvoice.Accounting", "modelId": "acc", "targetDocumentTypeMetaId": "invc" }
            }
            """;

        var graph = ModelGraph.Parse([
            ("Common/model.json", CommonJson),
            ("Accounting/model.json", accounting),
            ("Production/model.json", production),
            ("Sales/model.json", sales),
            ("Production/Dictionaries/BillOfMaterials/BillOfMaterials.object.json", bom),
            ("Sales/Documents/SalesInvoice/SalesInvoice.object.json", invoice),
            ("Accounting/DictionaryExtensions/BillOfMaterials.Accounting/BillOfMaterials.Accounting.extension.json", bomExt),
            ("Accounting/DocumentExtensions/SalesInvoice.Accounting/SalesInvoice.Accounting.extension.json", invoiceExt),
        ]).ToDictionary(n => n.Name);

        Assert.Contains("Common", graph["Accounting"].DependsOn);
        Assert.DoesNotContain("Production", graph["Accounting"].DependsOn);
        Assert.DoesNotContain("Sales", graph["Accounting"].DependsOn);
        Assert.Contains("Production", graph["Accounting"].Extends);
        Assert.Contains("Sales", graph["Accounting"].Extends);
    }

    [Fact]
    public void Expand_does_not_pull_extension_target_models()
    {
        var accounting = """
            {
              "object": { "name": "Accounting", "modelVersion": "1.0.0", "isSystem": false, "metaId": "acc" },
              "dependencies": [
                { "dependsOnModelMetaId": "aa", "name": "Accounting->Common" }
              ]
            }
            """;
        var production = """
            {
              "object": { "name": "Production", "modelVersion": "1.0.0", "isSystem": false, "metaId": "prod" },
              "dependencies": []
            }
            """;
        var bom = """
            {
              "kind": "Dictionary",
              "object": { "name": "BillOfMaterials", "metaId": "bom", "modelId": "prod" }
            }
            """;
        var bomExt = """
            {
              "kind": "DictionaryExtension",
              "object": { "name": "BillOfMaterials.Accounting", "modelId": "acc", "targetDictionaryMetaId": "bom" }
            }
            """;

        var graph = ModelGraph.Parse([
            ("Common/model.json", CommonJson),
            ("Core/model.json", CoreJson),
            ("Accounting/model.json", accounting),
            ("Production/model.json", production),
            ("Production/Dictionaries/BillOfMaterials/BillOfMaterials.object.json", bom),
            ("Accounting/DictionaryExtensions/BillOfMaterials.Accounting/BillOfMaterials.Accounting.extension.json", bomExt),
        ]).ToDictionary(n => n.Name, StringComparer.OrdinalIgnoreCase);

        var expanded = ModelGraph.Expand(["Accounting"], graph);

        Assert.Contains("Accounting", expanded);
        Assert.Contains("Common", expanded);
        Assert.Contains("Core", expanded);
        Assert.DoesNotContain("Production", expanded);
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
    public void ProblemsFor_ignores_siblings_outside_the_install_set()
    {
        string[] problems =
        [
            "LocalizationSaudiArabia: Failed CS0246",
            "LocalizationUkraine: DependencyFailed",
        ];

        var ofFive = ModelGraph.ProblemsFor(problems, ["Costing", "HR", "Organization", "Production", "Tax"]);
        Assert.Empty(ofFive);

        var ofUkraine = ModelGraph.ProblemsFor(problems, ["LocalizationUkraine", "Inventory", "Sales"]);
        Assert.Equal(["LocalizationUkraine: DependencyFailed"], ofUkraine);

        var everything = ModelGraph.ProblemsFor(problems, []);
        Assert.Equal(2, everything.Count);
    }

    [Fact]
    public void Parse_drops_the_seeded_stand_model()
    {
        const string local = """
            {
              "object": {
                "name": "Local",
                "modelVersion": "1.0.0",
                "isSystem": false,
                "metaId": "7e2c1f0a-9b4d-4e6a-8c3f-1d5a7b9e2c40"
              }
            }
            """;
        const string renamed = """
            {
              "object": {
                "name": "Okrasheno",
                "modelVersion": "1.0.0",
                "isSystem": false,
                "metaId": "7e2c1f0a-9b4d-4e6a-8c3f-1d5a7b9e2c40"
              }
            }
            """;

        var graph = ModelGraph.Parse([
            ("Common/model.json", CommonJson),
            ("Local/model.json", local),
            ("Okrasheno/model.json", renamed),
        ]);

        Assert.Contains(graph, n => n.Name == "Common");
        Assert.DoesNotContain(graph, n => n.Name == "Local");
        Assert.DoesNotContain(graph, n => n.Name == "Okrasheno");
    }

    [Fact]
    public void ParseModels_drops_Local_from_a_leaked_label()
    {
        var models = RegistryModelCatalog.ParseModels("Common=1.2.3,Local=1.0.0,Sales=1.3.6");
        Assert.Equal(["Common", "Sales"], models.Select(m => m.Name).ToList());
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
