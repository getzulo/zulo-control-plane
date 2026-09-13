namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>
/// One Mongo process for the farm, one database per tenant — the same isolation
/// shape as <c>tenant_{slug}</c> on Postgres.
/// </summary>
public static class TenantLogNames
{
    public const string Collection = "events";

    public static string Database(string slug) => $"logs_{slug}";

    public static string User(string slug) => $"logs_{slug}";

    public static bool IsLogDatabase(string name) =>
        name.StartsWith("logs_", StringComparison.Ordinal);
}
