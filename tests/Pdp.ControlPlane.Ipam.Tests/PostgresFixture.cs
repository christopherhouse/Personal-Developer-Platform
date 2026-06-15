using System.Net;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;
using Respawn;
using Testcontainers.PostgreSql;

namespace Pdp.ControlPlane.Ipam.Tests;

/// <summary>
/// A throwaway real Postgres for the IPAM tests — the GiST exclusion constraint and the
/// advisory-lock allocator cannot be exercised in-memory (constitution / FR-016). Started
/// once per test run; the full schema (extension + exclusion constraints + bootstrap seed) is
/// applied via the production migration. Data is reset between tests with Respawn and the
/// migration's bootstrap seed is re-applied so every test starts from a freshly-migrated
/// baseline.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Pinned image so the engine behaviour the tests rely on (btree_gist, inet_ops) is stable.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    private Respawner _respawner = null!;

    /// <summary>The container's connection string (set after <see cref="InitializeAsync"/>).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Deterministic id of the seeded platform-shared pool (index 0).</summary>
    public static Guid PlatformPoolId { get; } = Guid.Parse(IpamSeedData.PlatformPoolId);

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply the production migration: schema + btree_gist + exclusion constraints + seed.
        await using (var ctx = CreateContext())
        {
            await ctx.Database.MigrateAsync();
        }

        // Snapshot the table graph once; Respawn inspects schema, not data, so the presence of
        // seed rows here is irrelevant.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = ["public"],
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A fresh context bound to the container. Caller owns disposal.</summary>
    public IpamDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<IpamDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new IpamDbContext(options);
    }

    /// <summary>
    /// Wipe all data, then restore the migration's bootstrap seed (platform pool +
    /// control-plane-vnet reservation) so each test starts from the same baseline a freshly
    /// migrated database has.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);

        await using var ctx = CreateContext();
        await ctx.Database.ExecuteSqlRawAsync(IpamSeedData.Sql);
    }

    /// <summary>
    /// Seed a region pool directly — bypassing <c>register_region</c>, which arrives in Phase 5
    /// — so the US2 allocation tests have a pool to draw from. Derives the <c>/16</c> supernet
    /// and the <c>/22</c> hub carve-out from the index exactly as register_region will
    /// (data-model §1). The supernet non-overlap exclusion constraint still applies.
    /// </summary>
    public async Task<RegionPool> SeedRegionAsync(string region, short index)
    {
        await using var ctx = CreateContext();
        var pool = new RegionPool
        {
            Id = Guid.NewGuid(),
            Region = region,
            RegionIndex = index,
            Supernet = IPNetwork.Parse($"10.{index}.0.0/16"),
            HubCarveout = IPNetwork.Parse($"10.{index}.252.0/22"),
            CreatedAt = DateTimeOffset.UtcNow,
        };
        ctx.RegionPools.Add(pool);
        await ctx.SaveChangesAsync();
        return pool;
    }
}
