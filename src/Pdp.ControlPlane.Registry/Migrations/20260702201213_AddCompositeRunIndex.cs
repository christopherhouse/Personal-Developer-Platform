using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdp.ControlPlane.Registry.Migrations
{
    /// <inheritdoc />
    public partial class AddCompositeRunIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provisioning_runs_env_id",
                schema: "registry",
                table: "provisioning_runs");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_runs_env_id_dispatched",
                schema: "registry",
                table: "provisioning_runs",
                columns: new[] { "env_id", "dispatched_at" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_provisioning_runs_env_id_dispatched",
                schema: "registry",
                table: "provisioning_runs");

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_runs_env_id",
                schema: "registry",
                table: "provisioning_runs",
                column: "env_id");
        }
    }
}
