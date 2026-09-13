using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// Persists each tenant's Mongo journal credentials so container recreation
    /// does not depend on credentials that existed only during provisioning.
    /// </summary>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260913080000_AddTenantLogDatabase")]
    public partial class AddTenantLogDatabase : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogDatabase",
                table: "Tenants",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogPassword",
                table: "Tenants",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LogUser",
                table: "Tenants",
                type: "character varying(63)",
                maxLength: 63,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogDatabase",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogPassword",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "LogUser",
                table: "Tenants");
        }
    }
}
