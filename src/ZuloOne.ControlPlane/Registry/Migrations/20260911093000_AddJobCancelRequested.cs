using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ZuloOne.ControlPlane.Registry.Migrations
{
    /// <summary>
    /// A stop button for a running job.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Needed by the rollout, which is one job that moves many tenants and can
    /// therefore hold the strictly-serial queue for hours. Until now the only way to
    /// halt one was restarting the control plane, and recovery then marks it Failed —
    /// indistinguishable from a job that actually broke.
    /// </para>
    ///
    /// <para>
    /// Hand-written: dotnet-ef is not installed on this machine. Without the
    /// <c>[Migration]</c> id EF does not discover the migration at all.
    /// </para>
    /// </remarks>
    [DbContext(typeof(ControlPlaneDbContext))]
    [Migration("20260911093000_AddJobCancelRequested")]
    public partial class AddJobCancelRequested : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelRequested",
                table: "Jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CancelRequested",
                table: "Jobs");
        }
    }
}
