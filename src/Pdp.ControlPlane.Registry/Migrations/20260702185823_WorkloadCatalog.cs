using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdp.ControlPlane.Registry.Migrations
{
    /// <inheritdoc />
    public partial class WorkloadCatalog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "archetypes",
                schema: "registry",
                columns: table => new
                {
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archetypes", x => x.name);
                });

            migrationBuilder.CreateTable(
                name: "catalog_syncs",
                schema: "registry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: false),
                    summary = table.Column<string>(type: "jsonb", nullable: false),
                    applied_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_catalog_syncs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "archetype_versions",
                schema: "registry",
                columns: table => new
                {
                    archetype_name = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: false),
                    module_path = table.Column<string>(type: "text", nullable: false),
                    parameter_schema = table.Column<string>(type: "jsonb", nullable: false),
                    content_hash = table.Column<string>(type: "text", nullable: false),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_archetype_versions", x => new { x.archetype_name, x.version });
                    table.ForeignKey(
                        name: "fk_archetype_versions_archetypes_archetype_name",
                        column: x => x.archetype_name,
                        principalSchema: "registry",
                        principalTable: "archetypes",
                        principalColumn: "name",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "workloads",
                schema: "registry",
                columns: table => new
                {
                    env_id = table.Column<Guid>(type: "uuid", nullable: false),
                    spoke_subscription = table.Column<string>(type: "text", nullable: false),
                    spoke_name = table.Column<string>(type: "text", nullable: false),
                    archetype_name = table.Column<string>(type: "text", nullable: false),
                    archetype_version = table.Column<string>(type: "text", nullable: false),
                    pdp_env = table.Column<string>(type: "text", nullable: false),
                    parameters = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_workloads", x => x.env_id);
                    table.ForeignKey(
                        name: "fk_workloads_archetype_versions_archetype_name_archetype_versi",
                        columns: x => new { x.archetype_name, x.archetype_version },
                        principalSchema: "registry",
                        principalTable: "archetype_versions",
                        principalColumns: new[] { "archetype_name", "version" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_workloads_environments_env_id",
                        column: x => x.env_id,
                        principalSchema: "registry",
                        principalTable: "environments",
                        principalColumn: "env_id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_workloads_archetype_name_archetype_version",
                schema: "registry",
                table: "workloads",
                columns: new[] { "archetype_name", "archetype_version" });

            migrationBuilder.CreateIndex(
                name: "ix_workloads_spoke",
                schema: "registry",
                table: "workloads",
                columns: new[] { "spoke_subscription", "spoke_name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "catalog_syncs",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "workloads",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "archetype_versions",
                schema: "registry");

            migrationBuilder.DropTable(
                name: "archetypes",
                schema: "registry");
        }
    }
}
