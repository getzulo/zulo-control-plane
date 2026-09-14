using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// Per-tenant flag: this container is the operator's own stand and may
    /// edit Zulo product models. Default false — existing customer tenants
    /// stay locked.
    /// </summary>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260914080000_AddTenantDeveloperStand")]
    public partial class AddTenantDeveloperStand : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DeveloperStand",
                table: "Tenants",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeveloperStand",
                table: "Tenants");
        }
    }
}
