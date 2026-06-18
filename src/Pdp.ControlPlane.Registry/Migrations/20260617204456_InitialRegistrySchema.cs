using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdp.ControlPlane.Registry.Migrations
{
    /// <inheritdoc />
    public partial class InitialRegistrySchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "registry");

            migrationBuilder.CreateTable(
                name: "environment_saga",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    current_phase = table.Column<string>(type: "text", nullable: true),
                    pending_confirmation = table.Column<bool>(type: "boolean", nullable: false),
                    current_run_id = table.Column<Guid>(type: "uuid", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environment_saga", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "environments",
                schema: "registry",
                columns: table => new
                {
                    env_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    subscription = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    owner = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    spoke_cidr = table.Column<IPNetwork>(type: "cidr", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_environments", x => x.env_id);
                });

            migrationBuilder.CreateTable(
                name: "provisioning_runs",
                schema: "registry",
                columns: table => new
                {
                    run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    env_id = table.Column<Guid>(type: "uuid", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    workflow_file = table.Column<string>(type: "text", nullable: false),
                    dispatch_inputs = table.Column<string>(type: "jsonb", nullable: false),
                    github_run_id = table.Column<long>(type: "bigint", nullable: true),
                    github_run_url = table.Column<string>(type: "text", nullable: true),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    plan_summary = table.Column<string>(type: "text", nullable: true),
                    dispatched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    tracked_by = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_provisioning_runs", x => x.run_id);
                    table.ForeignKey(
                        name: "fk_provisioning_runs_environments_env_id",
                        column: x => x.env_id,
                        principalSchema: "registry",
                        principalTable: "environments",
                        principalColumn: "env_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_environment_natural_key",
                schema: "registry",
                table: "environments",
                columns: new[] { "kind", "subscription", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_provisioning_runs_env_id",
                schema: "registry",
                table: "provisioning_runs",
                column: "env_id");

            migrationBuilder.CreateIndex(
                name: "uq_provisioning_run_github_run",
                schema: "registry",
                table: "provisioning_runs",
                columns: new[] { "env_id", "github_run_id" },
                unique: true,
                filter: "github_run_id IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "environment_saga",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "provisioning_runs",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "environments",
                schema: "registry");
        }
    }
}
