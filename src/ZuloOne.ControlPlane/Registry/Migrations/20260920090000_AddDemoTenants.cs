using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// Demo workspaces: what a tenant row needs to be one, and the table of
    /// requests for them.
    ///
    /// Every tenant column is nullable with no default, so existing rows read as
    /// "not a demo, no expiry" without being touched — the same shape
    /// <c>AddTenantModels</c> used for the same reason.
    /// </summary>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260920090000_AddDemoTenants")]
    public partial class AddDemoTenants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Demo",
                table: "Tenants",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiresAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAt",
                table: "Tenants",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DemoRequestId",
                table: "Tenants",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DemoRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    Company = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Locale = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    SourceIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    State = table.Column<string>(type: "text", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: true),
                    TenantSlug = table.Column<string>(type: "character varying(63)", maxLength: 63, nullable: true),
                    Note = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReadyAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DemoRequests", x => x.Id);
                    // No foreign key to Tenants, deliberately: the request outlives
                    // the workspace it was given, which is what makes it history.
                });

            // The reaper's only query, and it runs forever on a table that is
            // mostly not demos.
            migrationBuilder.CreateIndex(
                name: "IX_Tenants_Demo_ExpiresAt",
                table: "Tenants",
                columns: new[] { "Demo", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoRequests_State_CreatedAt",
                table: "DemoRequests",
                columns: new[] { "State", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoRequests_Email_CreatedAt",
                table: "DemoRequests",
                columns: new[] { "Email", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DemoRequests_SourceIp_CreatedAt",
                table: "DemoRequests",
                columns: new[] { "SourceIp", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(name: "DemoRequests");

            migrationBuilder.DropIndex(
                name: "IX_Tenants_Demo_ExpiresAt",
                table: "Tenants");

            migrationBuilder.DropColumn(name: "DemoRequestId", table: "Tenants");
            migrationBuilder.DropColumn(name: "ClaimedAt", table: "Tenants");
            migrationBuilder.DropColumn(name: "ExpiresAt", table: "Tenants");
            migrationBuilder.DropColumn(name: "Demo", table: "Tenants");
        }
    }
}
