using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <inheritdoc />
    public partial class AddNodeBackupsJson : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BackupsJson",
                table: "NodeHealth",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupsJson",
                table: "NodeHealth");
        }
    }
}
