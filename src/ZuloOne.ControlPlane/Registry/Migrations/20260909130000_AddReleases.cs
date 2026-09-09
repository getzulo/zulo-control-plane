using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// The record of which build was promoted to which release, and by whom.
    /// </summary>
    /// <remarks>
    /// Hand-written: dotnet-ef is not installed on this machine. Without the
    /// [Migration] id EF does not discover the migration at all.
    /// </remarks>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260909130000_AddReleases")]
    public partial class AddReleases : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Releases",
                columns: table => new
                {
                    // The version IS the identity, and the primary key is what makes
                    // a release immutable: a second promotion of the same number is
                    // refused by the database, not only by the check above it.
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    SourceTag = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Digest = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Repository = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PromotedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PromotedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Releases", x => x.Version);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "Releases");
        }
    }
}
