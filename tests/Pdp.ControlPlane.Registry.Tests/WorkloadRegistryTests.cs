using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pdp.ControlPlane.Registry.Catalog;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Shouldly;

namespace Pdp.ControlPlane.Registry.Tests;

/// <summary>
/// Workload managed-unit rows on real Postgres (T016): a workload <b>is</b> an <c>environments</c>
/// row (<c>kind='workload'</c>) riding the natural-key idempotency and single-flight machinery
/// unchanged (R4), plus a 1:1 <c>registry.workloads</c> detail row that cascades with it and pins
/// its stamped archetype version at the database (FR-005 backstop). All of this hangs off unique
/// indexes and FK actions — untestable in-memory (plan §Testing).
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class WorkloadRegistryTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    private const string Subscription = "22222222-2222-2222-2222-222222222222";
    private const string Region = "westus3";

    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Seeds the catalog projection the workload FK needs (demo/v1.0.0).</summary>
    private async Task SeedCatalogAsync()
    {
        await using var context = fixture.CreateRegistryContext();
        await new CatalogSynchronizer(context, NullLogger<CatalogSynchronizer>.Instance).SyncAsync("""
            {
              "$schemaVersion": 1,
              "archetypes": [
                {
                  "name": "demo",
                  "description": "A demo archetype.",
                  "status": "active",
                  "versions": [
                    {
                      "version": "v1.0.0",
                      "modulePath": "archetypes/demo",
                      "parameterSchema": { "type": "object", "additionalProperties": false }
                    }
                  ]
                }
              ]
            }
            """);
    }

    private async Task<Guid> CreateWorkloadAsync(string name, string pdpEnv = "dev")
    {
        await using var context = fixture.CreateRegistryContext();
        var registry = new EnvironmentRegistry(context);
        var envId = await registry.BeginCreateAsync(
            EnvironmentKind.Workload, Subscription, Region, name, "owner");

        context.Workloads.Add(new WorkloadDetails
        {
            EnvId = envId,
            SpokeSubscription = Subscription,
            SpokeName = "spoke1",
            ArchetypeName = "demo",
            ArchetypeVersion = "v1.0.0",
            PdpEnv = pdpEnv,
            Parameters = """{"containerImage":"nginx:latest"}""",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        return envId;
    }

    [Fact]
    public async Task Workload_natural_key_converges_and_single_flights_like_every_managed_unit()
    {
        await SeedCatalogAsync();
        var envId = await CreateWorkloadAsync("demo-api");

        // Non-terminal (Requested) → a second mutating claim is refused (FR-022a).
        await using (var context = fixture.CreateRegistryContext())
        {
            var ex = await Should.ThrowAsync<OperationInProgressException>(() =>
                new EnvironmentRegistry(context).BeginCreateAsync(
                    EnvironmentKind.Workload, Subscription, Region, "demo-api", "owner"));
            ex.EnvId.ShouldBe(envId);
        }

        // Terminal → a re-create converges on the same surrogate (FR-022), never a duplicate.
        await using (var context = fixture.CreateRegistryContext())
        {
            await new EnvironmentRegistry(context).TransitionAsync(envId, EnvironmentStatus.Destroyed);
        }

        await using (var context = fixture.CreateRegistryContext())
        {
            var reclaimed = await new EnvironmentRegistry(context).BeginCreateAsync(
                EnvironmentKind.Workload, Subscription, Region, "demo-api", "owner");
            reclaimed.ShouldBe(envId);
        }
    }

    [Fact]
    public async Task Workload_and_spoke_may_share_a_name_kind_is_part_of_the_natural_key()
    {
        await SeedCatalogAsync();
        var workloadId = await CreateWorkloadAsync("app1");

        await using var context = fixture.CreateRegistryContext();
        var spokeId = await new EnvironmentRegistry(context).BeginCreateAsync(
            EnvironmentKind.Spoke, Subscription, Region, "app1", "owner");

        spokeId.ShouldNotBe(workloadId);
        (await context.Environments.CountAsync(e => e.Name == "app1")).ShouldBe(2);
    }

    [Fact]
    public async Task Workload_kind_persists_as_lowercase_text_and_the_detail_row_round_trips()
    {
        await SeedCatalogAsync();
        var envId = await CreateWorkloadAsync("demo-api", pdpEnv: "dev");

        await using var verify = fixture.CreateRegistryContext();

        // Readable persisted intent: the kind is the text 'workload' (matches the tag/glossary term).
        var connection = verify.Database.GetDbConnection();
        await verify.Database.OpenConnectionAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT kind FROM registry.environments WHERE env_id = @id";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "id";
            parameter.Value = envId;
            command.Parameters.Add(parameter);
            (await command.ExecuteScalarAsync()).ShouldBe("workload");
        }

        var details = await verify.Workloads.SingleAsync(w => w.EnvId == envId);
        details.SpokeName.ShouldBe("spoke1");
        details.ArchetypeName.ShouldBe("demo");
        details.ArchetypeVersion.ShouldBe("v1.0.0");
        details.PdpEnv.ShouldBe("dev");
        details.Parameters.ShouldContain("nginx:latest");

        // The environment row carves no address space (VI — workloads allocate nothing).
        (await verify.Environments.SingleAsync(e => e.EnvId == envId)).SpokeCidr.ShouldBeNull();
    }

    [Fact]
    public async Task Deleting_the_environment_cascades_the_workload_detail_row()
    {
        await SeedCatalogAsync();
        var envId = await CreateWorkloadAsync("doomed");

        await using (var context = fixture.CreateRegistryContext())
        {
            var environment = await context.Environments.SingleAsync(e => e.EnvId == envId);
            context.Environments.Remove(environment);
            await context.SaveChangesAsync();
        }

        await using var verify = fixture.CreateRegistryContext();
        (await verify.Workloads.AnyAsync(w => w.EnvId == envId)).ShouldBeFalse();
    }

    [Fact]
    public async Task Stamped_archetype_version_cannot_be_deleted_from_under_a_workload()
    {
        await SeedCatalogAsync();
        await CreateWorkloadAsync("pinned");

        // The FK is Restrict: removing the referenced version (or its archetype, whose delete
        // cascades into versions) must fail at the database — the FR-005 stamp is unbreakable.
        await using var context = fixture.CreateRegistryContext();
        var archetype = await context.Archetypes.SingleAsync(a => a.Name == "demo");
        context.Archetypes.Remove(archetype);

        await Should.ThrowAsync<DbUpdateException>(() => context.SaveChangesAsync());
    }
}
