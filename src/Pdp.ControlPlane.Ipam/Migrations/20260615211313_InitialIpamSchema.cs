using System;
using System.Net;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pdp.ControlPlane.Ipam.Migrations
{
    /// <inheritdoc />
    public partial class InitialIpamSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gist", ",,");

            migrationBuilder.CreateTable(
                name: "region_pool",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    region_index = table.Column<short>(type: "smallint", nullable: false),
                    supernet = table.Column<IPNetwork>(type: "cidr", nullable: false),
                    hub_carveout = table.Column<IPNetwork>(type: "cidr", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_region_pool", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "allocation",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    pool_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    network = table.Column<IPNetwork>(type: "cidr", nullable: false),
                    prefix_length = table.Column<short>(type: "smallint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    allocated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_allocation", x => x.id);
                    table.ForeignKey(
                        name: "fk_allocation_region_pool_pool_id",
                        column: x => x.pool_id,
                        principalTable: "region_pool",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "uq_allocation_pool_name",
                table: "allocation",
                columns: new[] { "pool_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_region_pool_region",
                table: "region_pool",
                column: "region",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_region_pool_region_index",
                table: "region_pool",
                column: "region_index",
                unique: true);

            // The load-bearing guarantee (FR-006): no two allocations in the same pool may have
            // overlapping networks. EXCLUDE constraints cannot be modelled in EF — raw SQL,
            // requires btree_gist (for `pool_id WITH =`) and inet_ops (for cidr `&&`). Research §8.
            migrationBuilder.Sql(
                "ALTER TABLE allocation " +
                "ADD CONSTRAINT allocations_no_overlap " +
                "EXCLUDE USING gist (pool_id WITH =, network inet_ops WITH &&);");

            // Regional supernets are mutually non-overlapping — the same mechanism refuses an
            // overlapping region registration at the database (data-model §2, FR-011, US3).
            migrationBuilder.Sql(
                "ALTER TABLE region_pool " +
                "ADD CONSTRAINT region_pool_supernet_no_overlap " +
                "EXCLUDE USING gist (supernet inet_ops WITH &&);");

            // Bootstrap seed (data-model §5): platform-shared supernet + control-plane VNet
            // reservation, so the platform's own range is registered (Article VI, research §12).
            migrationBuilder.Sql(IpamSeedData.Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // DropTable cascades these, but drop them explicitly for a clean, symmetric Down.
            migrationBuilder.Sql("ALTER TABLE allocation DROP CONSTRAINT IF EXISTS allocations_no_overlap;");
            migrationBuilder.Sql("ALTER TABLE region_pool DROP CONSTRAINT IF EXISTS region_pool_supernet_no_overlap;");

            migrationBuilder.DropTable(
                name: "allocation");

            migrationBuilder.DropTable(
                name: "region_pool");
        }
    }
}
