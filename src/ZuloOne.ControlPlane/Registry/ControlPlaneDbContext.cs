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

    /// <summary>Each database node's last self-check, keyed by hostname.</summary>
    public DbSet<NodeHealth> NodeHealth => Set<NodeHealth>();

    /// <summary>Logical dumps held on the control plane's disk.</summary>
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The slug IS the subdomain, so two tenants cannot share one.
        modelBuilder.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        modelBuilder.Entity<Tenant>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<Tenant>().Property(t => t.Health).HasConversion<string>();

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
    }
}
