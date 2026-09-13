using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// A health row is no longer assumed to be a Patroni member. Role says what
    /// the name is for so the panel can show Mongo, the app host, etcd and itself
    /// beside Postgres.
    /// </summary>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260913120000_AddNodeHealthRole")]
    public partial class AddNodeHealthRole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Role",
                table: "NodeHealth",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Role",
                table: "NodeHealth");
        }
    }
}
