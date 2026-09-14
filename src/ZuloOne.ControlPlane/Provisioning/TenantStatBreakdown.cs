namespace ZuloOne.ControlPlane.Provisioning;

/// <summary>One process inside the tenant container, as the host <c>ps</c> sees it.</summary>
public sealed record ContainerProcess(
    int Pid, double CpuPercent, double MemoryPercent, long RssBytes, string? Elapsed, string Command);

/// <summary>One named counter from the container's cgroup memory stats.</summary>
public sealed record MemoryPart(string Name, long Bytes);

/// <summary>A public table, by on-disk size including indexes.</summary>
public sealed record TableSize(string Name, long Bytes);

/// <summary>A live backend in this tenant's database.</summary>
public sealed record DbSession(string User, string Application, string State, int? Seconds, string? Query);

/// <summary>
/// Turns Docker <c>top</c> rows and cgroup counters into the breakdown the
/// panel shows under the CPU/memory meters.
/// </summary>
public static class TenantStatBreakdown
{
    internal static readonly (string Key, string Label)[] MemoryKeys =
    [
        ("rss", "RSS"),
        // cgroup v2 "anon" is heap/stacks/JIT — not a process named Anonymous.
        ("anon", "App heap"),
        ("cache", "Page cache"),
        ("file", "File cache"),
        ("swap", "Swap"),
        ("mapped_file", "Mapped files"),
        ("file_mapped", "Mapped files"),
        ("unevictable", "Unevictable"),
        ("kernel_stack", "Kernel stack"),
        ("slab", "Slab"),
    ];

    public static IReadOnlyList<MemoryPart> FromCgroup(IDictionary<string, ulong>? stats)
    {
        if (stats is null || stats.Count == 0) return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var parts = new List<MemoryPart>();
        foreach (var (key, label) in MemoryKeys)
        {
            if (!stats.TryGetValue(key, out var value) || value == 0) continue;
            if (!seen.Add(label)) continue;
            parts.Add(new MemoryPart(label, (long)value));
        }
        return parts;
    }

    public static IReadOnlyList<ContainerProcess> ParseProcesses(
        IList<string>? titles, IList<IList<string>>? rows)
    {
        if (titles is null || rows is null || titles.Count == 0) return [];

        var heads = titles.Select(t => t.Trim()).ToList();
        int Col(params string[] names)
        {
            for (var i = 0; i < heads.Count; i++)
            {
                foreach (var name in names)
                {
                    if (heads[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return i;
                }
            }
            return -1;
        }

        var pidCol = Col("PID");
        var cpuCol = Col("%CPU", "PCPU");
        var memCol = Col("%MEM", "PMEM");
        var rssCol = Col("RSS");
        var elapsedCol = Col("ELAPSED", "ETIME");
        var cmdCol = Col("COMMAND", "CMD", "ARGS");
        if (pidCol < 0 && cmdCol < 0) return [];

        var parsed = new List<ContainerProcess>(rows.Count);
        foreach (var row in rows)
        {
            if (row is null || row.Count == 0) continue;
            var pid = ReadInt(row, pidCol);
            var command = Read(row, cmdCol);
            if (command.Length > 120) command = command[..117] + "…";
            parsed.Add(new ContainerProcess(
                pid,
                ReadDouble(row, cpuCol),
                ReadDouble(row, memCol),
                // POSIX ps reports RSS in kilobytes.
                ReadLong(row, rssCol) * 1024,
                NullIfEmpty(Read(row, elapsedCol)),
                command.Length > 0 ? command : "—"));
        }

        return parsed
            .OrderByDescending(p => p.RssBytes)
            .ThenByDescending(p => p.CpuPercent)
            .Take(15)
            .ToList();
    }

    private static string Read(IList<string> row, int index)
        => index >= 0 && index < row.Count ? row[index]?.Trim() ?? string.Empty : string.Empty;

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static int ReadInt(IList<string> row, int index)
        => int.TryParse(Read(row, index), out var n) ? n : 0;

    private static long ReadLong(IList<string> row, int index)
        => long.TryParse(Read(row, index), out var n) ? n : 0;

    private static double ReadDouble(IList<string> row, int index)
        => double.TryParse(Read(row, index), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? n : 0;
}
