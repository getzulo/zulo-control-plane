using ZuloOne.ControlPlane.Provisioning;
using Xunit;

namespace ZuloOne.ControlPlane.Tests;

public sealed class TenantStatBreakdownTests
{
    [Fact]
    public void ParseProcesses_reads_posix_columns_and_rss_kilobytes()
    {
        var titles = new[] { "PID", "%CPU", "%MEM", "RSS", "ELAPSED", "COMMAND" };
        IList<string>[] rows =
        [
            ["12", "1.5", "40.0", "1024", "02:11:03", "/app/ZuloOne.Core"],
            ["1", "0.0", "0.1", "128", "02:11:03", "/usr/bin/dumb-init"],
        ];

        var parsed = TenantStatBreakdown.ParseProcesses(titles, rows);
        Assert.Equal(2, parsed.Count);
        Assert.Equal(12, parsed[0].Pid);
        Assert.Equal(1.5, parsed[0].CpuPercent);
        Assert.Equal(1024 * 1024, parsed[0].RssBytes);
        Assert.Equal("/app/ZuloOne.Core", parsed[0].Command);
        Assert.Equal(1, parsed[1].Pid);
    }

    [Fact]
    public void ParseProcesses_accepts_ef_titles_without_rss()
    {
        var titles = new[] { "UID", "PID", "PPID", "C", "STIME", "TTY", "TIME", "CMD" };
        IList<string>[] rows = [["root", "7", "1", "0", "08:01", "?", "00:00:01", "dotnet ZuloOne.Core.dll"]];

        var parsed = TenantStatBreakdown.ParseProcesses(titles, rows);
        Assert.Single(parsed);
        Assert.Equal(7, parsed[0].Pid);
        Assert.Equal(0, parsed[0].RssBytes);
        Assert.Equal("dotnet ZuloOne.Core.dll", parsed[0].Command);
    }

    [Fact]
    public void FromCgroup_keeps_known_nonzero_keys_and_dedupes_labels()
    {
        var stats = new Dictionary<string, ulong>
        {
            ["rss"] = 100,
            ["cache"] = 20,
            ["mapped_file"] = 5,
            ["file_mapped"] = 5,
            ["pgfault"] = 999,
            ["anon"] = 80,
        };

        var parts = TenantStatBreakdown.FromCgroup(stats);
        Assert.Equal(["RSS", "App heap", "Page cache", "Mapped files"], parts.Select(p => p.Name));
        Assert.Equal([100L, 80L, 20L, 5L], parts.Select(p => p.Bytes));
    }
}
