using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Options;
using ZuloOne.ControlPlane.Provisioning;

namespace ZuloOne.ControlPlane.Snapshots;

/// <summary>Where dumps live and how many are kept.</summary>
public sealed class SnapshotSettings
{
    /// <summary>Directory inside the container; the deployment mounts a volume here.</summary>
    public string Path { get; set; } = "/var/zuloone/snapshots";

    /// <summary>
    /// Refuse to start a dump when the filesystem has less than this much free.
    /// A dump that fills the disk takes the control plane down with it, and the
    /// control plane is what an operator would use to fix that.
    /// </summary>
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;
}

/// <summary>
/// Runs <c>pg_dump</c> and <c>pg_restore</c> as child processes.
///
/// <para>
/// Not Npgsql: a logical dump is a format only these tools produce and consume,
/// and reimplementing either would be a much larger and much worse idea than
/// shelling out to the ones PostgreSQL ships.
/// </para>
/// </summary>
public sealed class PgTools
{
    private readonly TenantDatabaseSettings _db;
    private readonly ILogger<PgTools> _logger;

    public PgTools(IOptions<TenantDatabaseSettings> db, ILogger<PgTools> logger)
    {
        _db = db.Value;
        _logger = logger;
    }

    /// <summary>
    /// A libpq connection string — deliberately NOT the Npgsql one.
    ///
    /// <para>
    /// The host setting is a comma-separated list (<c>10.10.1.210,10.10.2.210</c>)
    /// so Npgsql can follow a failover. libpq understands the same list, but needs
    /// <c>target_session_attrs</c> spelled its own way; without it these tools would
    /// happily connect to whichever node answered first, and a restore would land on
    /// a hot standby and fail with "cannot execute CREATE TABLE in a read-only
    /// transaction" — intermittently, depending on which node was quicker.
    /// </para>
    /// </summary>
    public string ConnectionString(string database, string user, string password)
    {
        var sb = new StringBuilder();
        sb.Append("host=").Append(_db.Host)
          .Append(" port=").Append(_db.Port)
          .Append(" dbname=").Append(Quote(database))
          .Append(" user=").Append(Quote(user))
          .Append(" password=").Append(Quote(password))
          // Both directions want the primary: a dump from a lagging replica would
          // silently be a slightly older dump than the operator believes.
          .Append(" target_session_attrs=read-write");
        if (_db.RequireSsl) sb.Append(" sslmode=require");
        return sb.ToString();

        // libpq's own quoting: backslash-escape, wrap in single quotes.
        static string Quote(string value) =>
            "'" + value.Replace("\\", "\\\\").Replace("'", "\\'") + "'";
    }

    /// <summary>Custom-format dump of one database to <paramref name="destination"/>.</summary>
    public Task<(bool Ok, string Output)> DumpAsync(
        string connectionString, string destination, CancellationToken ct)
        => RunAsync("pg_dump", [
            "--dbname", connectionString,
            // Custom format: compressed, and restorable selectively.
            "--format=custom",
            // Owner and ACLs are deliberately NOT captured. The dump is restored
            // into a database owned by a DIFFERENT role, and carrying the original
            // owner would make pg_restore try to hand objects to a role the target
            // has nothing to do with — failing on every single object.
            "--no-owner",
            "--no-privileges",
            "--file", destination,
        ], ct);

    /// <summary>Loads a dump into an existing, empty database.</summary>
    public Task<(bool Ok, string Output)> RestoreAsync(
        string connectionString, string source, CancellationToken ct)
        => RunAsync("pg_restore", [
            "--dbname", connectionString,
            "--no-owner",
            "--no-privileges",
            // Single transaction: either the whole tenant arrives or none of it
            // does. Without this a failure halfway leaves a database that is
            // populated enough to look plausible and is missing tables.
            "--single-transaction",
            source,
        ], ct);

    private async Task<(bool Ok, string Output)> RunAsync(string tool, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = tool,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // ArgumentList, never a joined string: the connection string carries a
        // password that may contain spaces and quotes, and .NET's Windows-style
        // argument joining would mangle it.
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        process.ErrorDataReceived  += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // A cancelled dump must not leave pg_dump running against production
            // with nobody holding its handle.
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            throw;
        }

        string text;
        lock (output) text = output.ToString();

        // Scrubbed before it can reach a job log the panel renders. The connection
        // string is an argument, and pg_restore echoes its own invocation on error.
        text = Scrub(text);

        if (process.ExitCode != 0)
            _logger.LogWarning("{Tool} exited {Code}: {Output}", tool, process.ExitCode, text);

        return (process.ExitCode == 0, text);
    }

    /// <summary>
    /// Removes anything password-shaped from tool output before it is stored.
    /// pg_restore quotes the connection string back at you in some errors, and a
    /// job log is read in a browser by whoever is on call.
    /// </summary>
    private static string Scrub(string text) =>
        System.Text.RegularExpressions.Regex.Replace(
            text, @"password=('(?:[^'\\]|\\.)*'|\S+)", "password=***");
}
