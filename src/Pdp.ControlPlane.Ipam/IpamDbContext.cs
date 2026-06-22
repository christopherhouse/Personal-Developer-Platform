using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Ipam;

/// <summary>
/// The IPAM ledger's EF Core context: two tables (<c>region_pool</c>, <c>allocation</c>) in
/// the control-plane Postgres database. snake_case identifiers; IP ranges are native
/// <c>cidr</c> (<see cref="System.Net.IPNetwork"/>). The non-overlap invariant is a database
/// GiST exclusion constraint added in the initial migration — never modelled in app code
/// (data-model.md §3, FR-006). Spec 006 hosts/extends this context.
/// </summary>
public class IpamDbContext(DbContextOptions<IpamDbContext> options) : DbContext(options)
{
    /// <summary>The PostgreSQL schema this context owns (beside <c>registry</c> and Wolverine's
    /// <c>wolverine</c>), so least-privilege grants and teardown are per-schema (SC-010).</summary>
    public const string Schema = "ipam";

    /// <summary>Registered region pools (and the platform-shared supernet, index 0).</summary>
    public DbSet<RegionPool> RegionPools => Set<RegionPool>();

    /// <summary>Live allocations carved from the pools.</summary>
    public DbSet<Allocation> Allocations => Set<Allocation>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        // Centralized so every consumer (tests, design-time, the spec-006 host) gets snake_case
        // without having to remember to opt in at registration.
        => optionsBuilder.UseSnakeCaseNamingConvention();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // This context's tables live in the `ipam` schema (mirrors RegistryDbContext), so the live
        // least-privilege UAMI grants and teardown are scoped per-schema (SC-010). Locally/tests this
        // was implicitly `public`; pinning it makes code match the grants/docs (the host-stack var and
        // the bootstrap GRANT both say `ipam`).
        modelBuilder.HasDefaultSchema(Schema);

        // btree_gist (brings B-tree equality `pool_id WITH =` into the GiST exclusion index alongside the
        // cidr overlap operator, research §8) is created directly in the migration as
        // `CREATE EXTENSION ... SCHEMA public`, NOT via HasPostgresExtension. Modelling it with an explicit
        // "public" schema yields a perpetual PendingModelChangesWarning — Npgsql drops the default-schema
        // qualifier from the snapshot but keeps it in the model diff. Raw SQL sidesteps that and keeps the
        // gist operator classes in `public`, in the default search_path where the EXCLUDE DDL resolves them.

        modelBuilder.Entity<RegionPool>(entity =>
        {
            entity.ToTable("region_pool");
            entity.HasKey(p => p.Id);

            entity.HasIndex(p => p.Region).IsUnique();
            entity.HasIndex(p => p.RegionIndex).IsUnique();

            entity.Property(p => p.Supernet).HasColumnType("cidr");
            entity.Property(p => p.HubCarveout).HasColumnType("cidr");

            entity.HasMany(p => p.Allocations)
                .WithOne(a => a.Pool!)
                .HasForeignKey(a => a.PoolId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Allocation>(entity =>
        {
            entity.ToTable("allocation");
            entity.HasKey(a => a.Id);

            // Idempotency key (FR-010): a name is unique within its pool. Constraint name is
            // referenced in tests/contracts as uq_allocation_pool_name.
            entity.HasIndex(a => new { a.PoolId, a.Name })
                .IsUnique()
                .HasDatabaseName("uq_allocation_pool_name");

            entity.Property(a => a.Network).HasColumnType("cidr");

            // Persist the enum as readable lowercase text ('spoke' | 'reservation') so a row's
            // intent is self-evident in the DB and matches the seed/contract literals.
            entity.Property(a => a.Kind)
                .HasConversion(
                    k => k.ToString().ToLowerInvariant(),
                    s => Enum.Parse<AllocationKind>(s, ignoreCase: true));
        });
    }
}
