using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Auth;
using ZuloOne.ControlPlane.Infra;
using ZuloOne.ControlPlane.Jobs;
using ZuloOne.ControlPlane.Snapshots;

namespace ZuloOne.ControlPlane.Registry;

/// <summary>
/// The control plane's own database — the tenant registry. Deliberately separate
/// from every tenant database: losing a tenant must not lose the record of the
/// fleet, and the registry is what the fleet is rebuilt from.
/// </summary>
public class ControlPlaneDbContext : DbContext
{
    public ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();

    public DbSet<OperatorAccount> OperatorAccounts => Set<OperatorAccount>();

    public DbSet<OperatorSession> OperatorSessions => Set<OperatorSession>();

    /// <summary>Every long-running operation the panel has performed, and its outcome.</summary>
    public DbSet<Job> Jobs => Set<Job>();

    /// <summary>Each fleet node's last self-check, keyed by the name it reported as.</summary>
    public DbSet<NodeHealth> NodeHealth => Set<NodeHealth>();

    /// <summary>Logical dumps held on the control plane's disk.</summary>
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();

    /// <summary>
    /// Builds somebody decided to call releases — the decision, not the image.
    /// The registry holds the bytes; this holds who, when, from what, and why.
    /// </summary>
    public DbSet<Release> Releases => Set<Release>();

    /// <summary>
    /// Configuration keys an operator has overridden in the panel. A row exists
    /// only where a deliberate decision was made — everything else falls through
    /// to cp.env and then to the compiled default.
    /// </summary>
    public DbSet<Settings.Setting> Settings => Set<Settings.Setting>();

    /// <summary>
    /// Requests for a demo workspace, including the ones that never got one.
    /// Both the queue and the record of who asked.
    /// </summary>
    public DbSet<DemoRequest> DemoRequests => Set<DemoRequest>();

    /// <summary>
    /// Customers who look after their own stands. Deliberately a different table
    /// from <see cref="OperatorAccount"/>, with different rules — see
    /// <see cref="Portal.CustomerAccount"/>.
    /// </summary>
    public DbSet<Portal.CustomerAccount> CustomerAccounts => Set<Portal.CustomerAccount>();

    public DbSet<Portal.CustomerSession> CustomerSessions => Set<Portal.CustomerSession>();

    /// <summary>One-shot links: confirm an address, set a new password.</summary>
    public DbSet<Portal.CustomerToken> CustomerTokens => Set<Portal.CustomerToken>();

    /// <summary>Which customer accounts may see and act on which stands.</summary>
    public DbSet<Portal.TenantMembership> TenantMemberships => Set<Portal.TenantMembership>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The slug IS the subdomain, so two tenants cannot share one.
        modelBuilder.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        modelBuilder.Entity<Tenant>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<Tenant>().Property(t => t.Health).HasConversion<string>();
        modelBuilder.Entity<Tenant>().Property(t => t.Origin).HasConversion<string>();
        modelBuilder.Entity<Tenant>().Property(t => t.Demo).HasConversion<string>();

        // The reaper's only query: rows that are demos and are out of time. It
        // runs on a timer forever, against a table that is mostly NOT demos, so
        // it is the one place here that would notice a missing index.
        modelBuilder.Entity<Tenant>().HasIndex(t => new { t.Demo, t.ExpiresAt });

        modelBuilder.Entity<DemoRequest>().Property(r => r.State).HasConversion<string>();
        // The queue, oldest first.
        modelBuilder.Entity<DemoRequest>().HasIndex(r => new { r.State, r.CreatedAt });
        // The two quota questions, both asked on every public request: has this
        // address already had a workspace today, and has this source address.
        modelBuilder.Entity<DemoRequest>().HasIndex(r => new { r.Email, r.CreatedAt });
        modelBuilder.Entity<DemoRequest>().HasIndex(r => new { r.SourceIp, r.CreatedAt });
        // No foreign key to Tenant. A request outlives the workspace it was given
        // — that is the entire point of keeping it.

        // Stored as text, like the tenant's enums: a job history is read by humans
        // during an incident, and an integer there means consulting the source to
        // find out what kind 3 was.
        modelBuilder.Entity<Job>().Property(j => j.Kind).HasConversion<string>();
        modelBuilder.Entity<Job>().Property(j => j.State).HasConversion<string>();
        // The two queries that exist: the worker's sweep for unfinished work at
        // start-up, and the panel listing a tenant's history newest-first.
        modelBuilder.Entity<Job>().HasIndex(j => j.State);
        modelBuilder.Entity<Job>().HasIndex(j => new { j.TenantId, j.CreatedAt });
        // NOT a foreign key to Tenant. A Restore outlives the tenant it came from
        // and a Delete outlives its subject entirely; a cascade would erase exactly
        // the history someone is looking for, and a restrict would block the delete.
        modelBuilder.Entity<Job>().Ignore(j => j.IsTerminal);

        modelBuilder.Entity<Snapshot>().Property(s => s.Kind).HasConversion<string>();
        // The list the panel shows: this tenant's snapshots, newest first.
        modelBuilder.Entity<Snapshot>().HasIndex(s => new { s.TenantId, s.CreatedAt });
        // No foreign key to Tenant, for the same reason as Jobs — a snapshot exists
        // precisely so it can outlive trouble that befalls its tenant, including the
        // tenant being deleted.

        modelBuilder.Entity<OperatorAccount>().HasIndex(a => a.Email).IsUnique();

        // Indexed and unique: this is looked up on every authenticated request, and
        // the uniqueness is what makes a lookup by hash unambiguous.
        modelBuilder.Entity<OperatorSession>().HasIndex(s => s.TokenHash).IsUnique();
        // Cascade so that removing the account cannot leave sessions that would
        // still authenticate against nothing.
        modelBuilder.Entity<OperatorSession>()
            .HasOne(s => s.Account)
            .WithMany()
            .HasForeignKey(s => s.OperatorAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        // ------------------------------------------------------------ portal ---

        // Unique and the login key. Addresses are lower-cased on write precisely so
        // that this index can be the thing that stops one person becoming two
        // accounts, only one of which owns their stand.
        modelBuilder.Entity<Portal.CustomerAccount>().HasIndex(a => a.Email).IsUnique();

        modelBuilder.Entity<Portal.CustomerSession>().HasIndex(s => s.TokenHash).IsUnique();
        modelBuilder.Entity<Portal.CustomerSession>()
            .HasOne(s => s.Account)
            .WithMany()
            .HasForeignKey(s => s.CustomerAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Portal.CustomerToken>().Property(t => t.Kind).HasConversion<string>();
        // Looked up by hash on every click of a link in an e-mail.
        modelBuilder.Entity<Portal.CustomerToken>().HasIndex(t => t.TokenHash).IsUnique();
        modelBuilder.Entity<Portal.CustomerToken>()
            .HasOne(t => t.Account)
            .WithMany()
            .HasForeignKey(t => t.CustomerAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Portal.TenantMembership>().Property(m => m.Role).HasConversion<string>();
        // One grant per person per stand: the role is a column on the row, not a
        // second row, so a duplicate would mean two different answers to "may
        // they?" with nothing deciding which wins.
        modelBuilder.Entity<Portal.TenantMembership>()
            .HasIndex(m => new { m.CustomerAccountId, m.TenantId }).IsUnique();
        modelBuilder.Entity<Portal.TenantMembership>()
            .HasOne(m => m.Account)
            .WithMany()
            .HasForeignKey(m => m.CustomerAccountId)
            .OnDelete(DeleteBehavior.Cascade);
        // A real foreign key to Tenant WITH cascade — unlike Jobs and Snapshots,
        // which have none because they are history and must outlive their tenant.
        // A membership is access, and access to a tenant that no longer exists is
        // a dangling grant the portal would list and then fail on.
        modelBuilder.Entity<Portal.TenantMembership>()
            .HasOne(m => m.Tenant)
            .WithMany()
            .HasForeignKey(m => m.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
