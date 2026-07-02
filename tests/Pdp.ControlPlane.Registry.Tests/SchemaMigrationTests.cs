using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Pdp.ControlPlane.Registry;
using Shouldly;
using Testcontainers.PostgreSql;

namespace Pdp.ControlPlane.Registry.Tests;

/// <summary>
/// Proves the <c>InitialRegistrySchema</c> migration is reversible and leaves no residual footprint
/// (the schema half of SC-010 / Article IV): applying it creates the <c>registry</c> tables, and
/// migrating back to <c>0</c> (the equivalent of <c>dotnet ef database update 0</c>) drops them
/// cleanly. Runs on its own throwaway container so the destructive down-migration is isolated from
/// the shared fixture.
/// </summary>
public sealed class SchemaMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container =
        new PostgreSqlBuilder("postgres:17-alpine").Build();

    public Task InitializeAsync() => _container.StartAsync();

    public async Task DisposeAsync() => await _container.DisposeAsync();

    private RegistryDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<RegistryDbContext>()
            .UseNpgsql(_container.GetConnectionString())
            .Options;
        return new RegistryDbContext(options);
    }

    [Fact]
    public async Task Migration_applies_registry_schema_then_drops_it_cleanly()
    {
        await using var context = CreateContext();

        // Apply: the production migration creates the registry tables.
        await context.Database.MigrateAsync();

        (await TableExistsAsync(context, "environments")).ShouldBeTrue();
        (await TableExistsAsync(context, "provisioning_runs")).ShouldBeTrue();
        (await TableExistsAsync(context, "environment_saga")).ShouldBeTrue();
        // Spec 008 (WorkloadCatalog migration): catalog projection + workload detail.
        (await TableExistsAsync(context, "archetypes")).ShouldBeTrue();
        (await TableExistsAsync(context, "archetype_versions")).ShouldBeTrue();
        (await TableExistsAsync(context, "catalog_syncs")).ShouldBeTrue();
        (await TableExistsAsync(context, "workloads")).ShouldBeTrue();

        // Teardown: migrate back to 0 — the equivalent of `dotnet ef database update 0`.
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(Migration.InitialDatabase);

        // The schema's tables are gone — no residual footprint (SC-010).
        (await TableExistsAsync(context, "environments")).ShouldBeFalse();
        (await TableExistsAsync(context, "provisioning_runs")).ShouldBeFalse();
        (await TableExistsAsync(context, "environment_saga")).ShouldBeFalse();
        (await TableExistsAsync(context, "archetypes")).ShouldBeFalse();
        (await TableExistsAsync(context, "archetype_versions")).ShouldBeFalse();
        (await TableExistsAsync(context, "catalog_syncs")).ShouldBeFalse();
        (await TableExistsAsync(context, "workloads")).ShouldBeFalse();
    }

    // to_regclass returns NULL when the relation does not exist (no exception). Cast to text so
    // Npgsql can read it — the regclass OID type is not readable as a CLR object.
    private static async Task<bool> TableExistsAsync(RegistryDbContext context, string table)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT to_regclass('{RegistryDbContext.Schema}.{table}')::text";
        var result = await command.ExecuteScalarAsync();
        return result is string;
    }
}
