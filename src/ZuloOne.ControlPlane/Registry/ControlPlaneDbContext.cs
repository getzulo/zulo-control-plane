using Microsoft.EntityFrameworkCore;
using ZuloOne.ControlPlane.Auth;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The slug IS the subdomain, so two tenants cannot share one.
        modelBuilder.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        modelBuilder.Entity<Tenant>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<Tenant>().Property(t => t.Health).HasConversion<string>();

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
