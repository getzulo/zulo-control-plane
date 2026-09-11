using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// Which models a tenant installs, per tenant, instead of one list for the fleet.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable with NO default, and that is the point rather than an omission. NULL
    /// means "follow <c>Fleet:Packages</c>", so every existing tenant keeps the
    /// fleet-wide list it has always had and nothing changes until somebody pins one
    /// deliberately. A backfill here would silently freeze every tenant on whatever
    /// the fleet list happened to say the day this ran.
    /// </para>
    ///
    /// <para>
    /// Hand-written: dotnet-ef is not installed on this machine. The attributes below
    /// are what a Designer file would normally carry, and without the
    /// <c>[Migration]</c> id EF does not discover the migration at all.
    /// </para>
    /// </remarks>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260911090000_AddTenantModels")]
    public partial class AddTenantModels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Models",
                table: "Tenants",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Models",
                table: "Tenants");
        }
    }
}
