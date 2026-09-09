using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// Overrides an operator has set in the panel, one row per key.
    /// </summary>
    /// <remarks>
    /// Hand-written: dotnet-ef is not installed on this machine. The attributes
    /// are what a Designer file would carry, and without the [Migration] id EF
    /// does not discover the migration at all.
    /// </remarks>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260909120000_AddSettings")]
    public partial class AddSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    // The configuration key IS the identity — colon-separated,
                    // spelled exactly as the configuration layer spells it, so the
                    // two cannot disagree about which knob this is.
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Value = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Settings");
        }
    }
}
