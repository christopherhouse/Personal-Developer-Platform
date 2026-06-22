using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Ipam;
using Respawn;
using Testcontainers.PostgreSql;

namespace Pdp.ControlPlane.TestSupport;

/// <summary>
/// A throwaway real Postgres shared across the control-plane integration tests. The ledger
/// (GiST/advisory-lock), the registry (natural key, single-flight), and Wolverine's outbox/saga are
/// untestable in-memory (constitution / plan §Testing), so every test that touches them runs against
/// this container. Started once per test run; <b>both</b> the IPAM and registry production migrations
/// are applied so the schema is exactly what ships. Data is reset between tests with Respawn and the
/// IPAM bootstrap seed is re-applied, so each test starts from a freshly-migrated baseline. Mirrors
/// the IPAM <c>PostgresFixture</c>, extended to host the registry schema too.
/// </summary>
public sealed class ControlPlanePostgresFixture : IAsyncLifetime
{
    // Pinned image so the engine behaviour the tests rely on (btree_gist, inet_ops) is stable.
    // max_connections headroom (default 100): each test disposes its host so only one Wolverine host's
    // pool is live at a time, but the durability agent + EF pools + Respawn/Npgsql briefly overlap during
    // host start/stop; the higher ceiling keeps a loaded CI agent off the "too many clients" edge (the
    // disposal in each test's teardown is the actual leak fix — this is defense in depth).
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .WithCommand("-c", "max_connections=300")
        .Build();

    private Respawner _respawner = null!;

    /// <summary>The container's connection string (set after <see cref="InitializeAsync"/>).</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Apply both production migrations onto the one database. Each context's tables live in their
        // own schema (ipam / registry); migrations are tracked in __EFMigrationsHistory keyed by
        // migration_id, so applying both onto one database never collides.
        await using (var ipam = CreateIpamContext())
        {
            await ipam.Database.MigrateAsync();
        }

        await using (var registry = CreateRegistryContext())
        {
            await registry.Database.MigrateAsync();
        }

        // Snapshot the table graph once; Respawn inspects schema, not data, so the presence of the
        // ipam seed rows is irrelevant.
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        _respawner = await Respawner.CreateAsync(conn, new RespawnerOptions
        {
            DbAdapter = DbAdapter.Postgres,
            SchemasToInclude = [IpamDbContext.Schema, Registry.RegistryDbContext.Schema],
            TablesToIgnore = ["__EFMigrationsHistory"],
        });
    }

    /// <inheritdoc />
    public async Task DisposeAsync() => await _container.DisposeAsync();

    /// <summary>A fresh IPAM context bound to the container. Caller owns disposal.</summary>
    public IpamDbContext CreateIpamContext()
    {
        var options = new DbContextOptionsBuilder<IpamDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new IpamDbContext(options);
    }

    /// <summary>A fresh registry context bound to the container. Caller owns disposal.</summary>
    public Registry.RegistryDbContext CreateRegistryContext()
    {
        var options = new DbContextOptionsBuilder<Registry.RegistryDbContext>()
            .UseNpgsql(ConnectionString)
            .Options;
        return new Registry.RegistryDbContext(options);
    }

    /// <summary>
    /// Wipe all data across the ipam + registry schemas, then restore the IPAM migration's bootstrap
    /// seed (platform pool + control-plane-vnet reservation) so each test starts from the same
    /// baseline a freshly-migrated database has.
    /// </summary>
    public async Task ResetAsync()
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await _respawner.ResetAsync(conn);

        await using var ipam = CreateIpamContext();
        await ipam.Database.ExecuteSqlRawAsync(IpamSeedData.Sql);
    }
}
