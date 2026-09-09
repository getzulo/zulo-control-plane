using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// Records whether the panel built a tenant or took over an existing one, so
    /// Delete can tell the difference. See <c>TenantOrigin</c> for why status could
    /// never answer this.
    /// </summary>
    /// <remarks>
    /// Hand-written: dotnet-ef is not installed on this machine. The attributes
    /// below are what a Designer file would normally carry, and without the
    /// <c>[Migration]</c> id EF does not discover the migration at all.
    /// </remarks>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260909100000_AddTenantOrigin")]
    public partial class AddTenantOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Provisioned is the safe default for existing rows only because it is
            // the permissive one, and the backfill below immediately corrects it.
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Tenants",
                type: "text",
                nullable: false,
                defaultValue: "Provisioned");

            // A tenant whose database was never created by this panel is adopted.
            // The registry does not record that historically, so infer it: an
            // adopted row has no AdminPasswordOnce, because the panel never seeded
            // its administrator — provisioning is the only path that writes one.
            //
            // Deliberately conservative in the safe direction. Marking a
            // provisioned tenant as adopted costs one extra step to delete it;
            // the reverse costs a database.
            migrationBuilder.Sql(@"
                UPDATE ""Tenants""
                SET ""Origin"" = 'Adopted'
                WHERE ""AdminPasswordOnce"" IS NULL
                  AND ""RestoredFromSlug"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Tenants");
        }
    }
}
