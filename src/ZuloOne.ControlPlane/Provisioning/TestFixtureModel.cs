namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// In-app integration-test fixtures. They live in the workspace so CI can
/// prove the tree, and they must not be a product a customer can install.
/// </summary>
/// <remarks>
/// Dist CI already omits them (<c>treeExclude</c>). This type is the panel
/// half: old images still label <c>TestBench=…</c>, a select-all install
/// packed the whole tree, and the catalogue then offered the junk. Names
/// and ids stay in lock-step with Core's <c>TestFixtureModel</c>.
/// </remarks>
public static class TestFixtureModel
{
    public const string TestBenchMetaId = "0ed6ef68-ff4d-4d38-9d6c-9ee31d38bd29";
    public const string TestBenchExtMetaId = "390cbabb-1f33-47db-afd7-b78fb78afae9";

    public static bool IsName(string? name) =>
        string.Equals(name, "TestBench", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "TestBenchExt", StringComparison.OrdinalIgnoreCase);

    public static bool IsMetaId(string? metaId) =>
        Guid.TryParse(metaId, out var id)
        && (id == Guid.Parse(TestBenchMetaId) || id == Guid.Parse(TestBenchExtMetaId));
}
