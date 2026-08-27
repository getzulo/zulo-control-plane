using Microsoft.EntityFrameworkCore;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // The slug IS the subdomain, so two tenants cannot share one.
        modelBuilder.Entity<Tenant>().HasIndex(t => t.Slug).IsUnique();
        modelBuilder.Entity<Tenant>().Property(t => t.Status).HasConversion<string>();
        modelBuilder.Entity<Tenant>().Property(t => t.Health).HasConversion<string>();
    }
}
