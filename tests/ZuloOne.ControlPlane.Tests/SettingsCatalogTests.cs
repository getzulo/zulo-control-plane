using ZuloOne.ControlPlane.Settings;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class SettingsCatalogTests
{
    private static SettingDef[] Demo =>
        SettingsCatalog.All.Where(d => d.Group == SettingsCatalog.Demo).ToArray();

    [Fact]
    public void Demo_group_declares_the_policy_knobs()
    {
        string[] expected =
        [
            "Demo:Enabled",
            "Demo:GoldenSlug",
            "Demo:TemplateSnapshotId",
            "Demo:PoolTarget",
            "Demo:MaxConcurrent",
            "Demo:MaxQueue",
            "Demo:LifetimeHours",
            "Demo:PoolMaxAgeHours",
            "Demo:MaxExtendHours",
            "Demo:ReapIntervalSeconds",
            "Demo:MaxPerEmailPerDay",
            "Demo:MaxPerIpPerDay",
            "Demo:PasswordWindowMinutes",
            "Demo:MemoryLimitBytes",
            "Demo:CpuMilli",
            "Demo:PidsLimit",
            "Demo:UserName",
        ];

        Assert.Equal(expected.Order(), Demo.Select(d => d.Key).Order());
    }

    /// <summary>
    /// The credential that opens the only anonymous write path must never become
    /// a field on a screen. It lives in cp.env, like Access:* and Mail:Password.
    /// </summary>
    [Fact]
    public void Demo_request_token_is_NOT_in_the_catalogue()
    {
        Assert.DoesNotContain(SettingsCatalog.All, d => d.Key == "Demo:RequestToken");

        // Matched on the key's last segment, not a substring: an earlier version
        // of this test used Contains("Password") and flagged
        // Demo:PasswordWindowMinutes, which is a duration. A secret-detector that
        // cries wolf gets deleted, and then it detects nothing.
        Assert.DoesNotContain(
            SettingsCatalog.All,
            d => d.Key.EndsWith(":Password", StringComparison.Ordinal)
                 || d.Key.EndsWith(":Token", StringComparison.Ordinal)
                 || d.Key.EndsWith(":Secret", StringComparison.Ordinal));
    }

    /// <summary>
    /// A default outside its own bounds ships silently: nothing validates the
    /// compiled-in value, and the screen only checks what an operator types.
    /// </summary>
    [Fact]
    public void Every_numeric_default_sits_within_its_own_bounds()
    {
        foreach (var def in SettingsCatalog.All)
        {
            if (def.Kind is not (SettingKind.Int or SettingKind.Bytes or SettingKind.Seconds
                or SettingKind.Hours or SettingKind.Days))
            {
                continue;
            }

            Assert.True(
                long.TryParse(def.Default, out var value),
                $"{def.Key}: default '{def.Default}' is not a number, but its kind is {def.Kind}");

            if (def.Min is { } min)
            {
                Assert.True(value >= min, $"{def.Key}: default {value} is below Min {min}");
            }

            if (def.Max is { } max)
            {
                Assert.True(value <= max, $"{def.Key}: default {value} is above Max {max}");
            }
        }
    }

    [Fact]
    public void Booleans_default_to_something_parseable()
    {
        foreach (var def in SettingsCatalog.All.Where(d => d.Kind == SettingKind.Bool))
        {
            Assert.True(
                bool.TryParse(def.Default, out _),
                $"{def.Key}: default '{def.Default}' is not a bool");
        }
    }

    [Fact]
    public void Keys_are_unique()
    {
        var duplicates = SettingsCatalog.All
            .GroupBy(d => d.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToArray();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// Every demo knob has to be reachable from the panel: the whole point of the
    /// catalogue is that capacity and lifetime can be changed during an incident
    /// without recreating the container.
    /// </summary>
    [Fact]
    public void Demo_policy_is_runtime_editable()
    {
        Assert.All(Demo, d => Assert.True(d.RuntimeEditable, $"{d.Key} is not runtime-editable"));
    }
}
