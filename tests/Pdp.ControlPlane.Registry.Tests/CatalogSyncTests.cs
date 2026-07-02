using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Pdp.ControlPlane.Registry.Catalog;
using Pdp.ControlPlane.Registry.Entities;
using Pdp.ControlPlane.TestSupport;
using Shouldly;

namespace Pdp.ControlPlane.Registry.Tests;

/// <summary>
/// Catalog sync semantics on real Postgres (T015 — contracts/archetype-catalog.md §Sync semantics):
/// first-sync insert, the unchanged-hash short-circuit, retire/reactivate status changes,
/// <b>version immutability rejection preserving the previous projection</b> (R2), invalid-file
/// rejection, and the <c>catalog_syncs</c> audit trail (FR-006). These behaviors ride the
/// database-enforced keys and transactions, so they are untestable in-memory (plan §Testing). This
/// proves the US4 data layer; US4's verb behavior and live pipeline are phase-6 work.
/// </summary>
[Collection(ControlPlaneCollection.Name)]
public sealed class CatalogSyncTests(ControlPlanePostgresFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => fixture.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CatalogSyncOutcome> SyncAsync(string catalogJson)
    {
        await using var context = fixture.CreateRegistryContext();
        var synchronizer = new CatalogSynchronizer(context, NullLogger<CatalogSynchronizer>.Instance);
        return await synchronizer.SyncAsync(catalogJson);
    }

    /// <summary>A minimal valid catalog file with one archetype and the given versions.</summary>
    private static string Catalog(
        string status = "active",
        string description = "A demo archetype.",
        params (string Version, string SchemaMinLength)[] versions)
    {
        if (versions.Length == 0)
        {
            versions = [("v1.0.0", "1")];
        }

        var versionEntries = string.Join(",", versions.Select(v => $$"""
            {
              "version": "{{v.Version}}",
              "modulePath": "archetypes/demo",
              "parameterSchema": {
                "type": "object",
                "additionalProperties": false,
                "required": ["containerImage"],
                "properties": {
                  "containerImage": { "type": "string", "minLength": {{v.SchemaMinLength}} }
                }
              }
            }
            """));

        return $$"""
            {
              "$schemaVersion": 1,
              "archetypes": [
                {
                  "name": "demo",
                  "description": "{{description}}",
                  "status": "{{status}}",
                  "versions": [{{versionEntries}}]
                }
              ]
            }
            """;
    }

    [Fact]
    public async Task First_sync_inserts_archetype_and_versions_and_records_applied_audit()
    {
        var outcome = await SyncAsync(Catalog());

        outcome.ShouldBe(CatalogSyncOutcome.Applied);

        await using var verify = fixture.CreateRegistryContext();
        var archetype = await verify.Archetypes.Include(a => a.Versions).SingleAsync();
        archetype.Name.ShouldBe("demo");
        archetype.Status.ShouldBe(ArchetypeStatus.Active);
        archetype.Versions.ShouldHaveSingleItem().Version.ShouldBe("v1.0.0");
        archetype.Versions[0].ModulePath.ShouldBe("archetypes/demo");
        archetype.Versions[0].ContentHash.ShouldNotBeNullOrWhiteSpace();

        var audit = await verify.CatalogSyncs.SingleAsync();
        audit.Outcome.ShouldBe(CatalogSyncOutcome.Applied);
        audit.Summary.ShouldContain("\"added\"");
        audit.Summary.ShouldContain("\"version-added\"");
    }

    [Fact]
    public async Task Unchanged_file_short_circuits_to_no_change()
    {
        var file = Catalog();

        (await SyncAsync(file)).ShouldBe(CatalogSyncOutcome.Applied);
        (await SyncAsync(file)).ShouldBe(CatalogSyncOutcome.NoChange);

        await using var verify = fixture.CreateRegistryContext();
        (await verify.Archetypes.CountAsync()).ShouldBe(1);
        var audits = await verify.CatalogSyncs.OrderBy(s => s.AppliedAt).ToListAsync();
        audits.Select(a => a.Outcome).ShouldBe(
            [CatalogSyncOutcome.Applied, CatalogSyncOutcome.NoChange]);
        audits[0].ContentHash.ShouldBe(audits[1].ContentHash);
    }

    [Fact]
    public async Task Retire_then_reactivate_apply_status_changes_and_audit_them()
    {
        (await SyncAsync(Catalog(status: "active"))).ShouldBe(CatalogSyncOutcome.Applied);
        (await SyncAsync(Catalog(status: "retired"))).ShouldBe(CatalogSyncOutcome.Applied);

        await using (var verify = fixture.CreateRegistryContext())
        {
            (await verify.Archetypes.SingleAsync()).Status.ShouldBe(ArchetypeStatus.Retired);
            (await verify.CatalogSyncs.OrderBy(s => s.AppliedAt).LastAsync())
                .Summary.ShouldContain("\"retired\"");
        }

        (await SyncAsync(Catalog(status: "active"))).ShouldBe(CatalogSyncOutcome.Applied);

        await using (var verify = fixture.CreateRegistryContext())
        {
            (await verify.Archetypes.SingleAsync()).Status.ShouldBe(ArchetypeStatus.Active);
            (await verify.CatalogSyncs.OrderBy(s => s.AppliedAt).LastAsync())
                .Summary.ShouldContain("\"reactivated\"");
        }
    }

    [Fact]
    public async Task Appended_version_registers_and_newest_resolves_by_semver_not_text()
    {
        (await SyncAsync(Catalog(versions: [("v1.9.0", "1")]))).ShouldBe(CatalogSyncOutcome.Applied);
        // v1.10.0 > v1.9.0 numerically but sorts lower as text — the resolution must be SemVer.
        (await SyncAsync(Catalog(versions: [("v1.9.0", "1"), ("v1.10.0", "2")])))
            .ShouldBe(CatalogSyncOutcome.Applied);

        await using var verify = fixture.CreateRegistryContext();
        var store = new CatalogStore(verify);

        (await store.ResolveNewestVersionAsync("demo"))!.Version.ShouldBe("v1.10.0");
        (await store.FindVersionAsync("demo", "v1.9.0"))!.Version.ShouldBe("v1.9.0");

        var listed = await store.ListAsync();
        listed.ShouldHaveSingleItem().Versions.Select(v => v.Version)
            .ShouldBe(["v1.10.0", "v1.9.0"]);
    }

    [Fact]
    public async Task Immutability_rejection_fails_the_whole_sync_and_preserves_the_previous_projection()
    {
        (await SyncAsync(Catalog(versions: [("v1.0.0", "1")]))).ShouldBe(CatalogSyncOutcome.Applied);

        string originalHash;
        await using (var before = fixture.CreateRegistryContext())
        {
            originalHash = (await before.ArchetypeVersions.SingleAsync()).ContentHash;
        }

        // Same (archetype, version) with DIFFERENT schema content + an otherwise-legal new version:
        // the whole sync must fail — the legitimate v1.1.0 append must NOT survive the rejection.
        var mutated = Catalog(versions: [("v1.0.0", "5"), ("v1.1.0", "1")]);
        (await SyncAsync(mutated)).ShouldBe(CatalogSyncOutcome.Rejected);

        await using var verify = fixture.CreateRegistryContext();
        var version = await verify.ArchetypeVersions.SingleAsync();
        version.Version.ShouldBe("v1.0.0");
        version.ContentHash.ShouldBe(originalHash);

        var rejection = await verify.CatalogSyncs.OrderBy(s => s.AppliedAt).LastAsync();
        rejection.Outcome.ShouldBe(CatalogSyncOutcome.Rejected);
        rejection.Summary.ShouldContain("immutable");
    }

    [Fact]
    public async Task Invalid_file_is_rejected_and_the_previous_projection_keeps_serving()
    {
        (await SyncAsync(Catalog())).ShouldBe(CatalogSyncOutcome.Applied);

        // Not JSON at all.
        (await SyncAsync("{ not json")).ShouldBe(CatalogSyncOutcome.Rejected);

        // Structurally legal JSON, but not a legal catalog (bad name, bad status, schema not a
        // valid draft 2020-12 document: "type": 42).
        (await SyncAsync("""
            {
              "$schemaVersion": 1,
              "archetypes": [
                {
                  "name": "Not Valid!",
                  "description": "bad",
                  "status": "paused",
                  "versions": [
                    { "version": "v1.0.0", "modulePath": "archetypes/x", "parameterSchema": { "type": 42 } }
                  ]
                }
              ]
            }
            """)).ShouldBe(CatalogSyncOutcome.Rejected);

        await using var verify = fixture.CreateRegistryContext();
        // The good projection is intact and the rejections are audited.
        (await verify.Archetypes.SingleAsync()).Name.ShouldBe("demo");
        var outcomes = await verify.CatalogSyncs.OrderBy(s => s.AppliedAt)
            .Select(s => s.Outcome).ToListAsync();
        outcomes.ShouldBe(
            [CatalogSyncOutcome.Applied, CatalogSyncOutcome.Rejected, CatalogSyncOutcome.Rejected]);
    }

    [Fact]
    public async Task Cosmetic_reformat_of_the_file_does_not_trip_immutability()
    {
        (await SyncAsync(Catalog())).ShouldBe(CatalogSyncOutcome.Applied);

        // Same semantic content, different whitespace → different whole-file hash (no short-circuit)
        // but identical canonical version hashes → applied with nothing to do, never rejected.
        var reformatted = Catalog().Replace("\r\n", "\n").Replace("\n", "\n ");
        (await SyncAsync(reformatted)).ShouldBe(CatalogSyncOutcome.Applied);

        await using var verify = fixture.CreateRegistryContext();
        (await verify.ArchetypeVersions.CountAsync()).ShouldBe(1);
    }

    [Fact]
    public async Task Seed_catalog_file_in_the_repo_parses_and_syncs()
    {
        // The real archetypes/catalog.json (T013) must satisfy the same gate the image build bakes in.
        var repoRoot = new DirectoryInfo(AppContext.BaseDirectory);
        while (repoRoot is not null && !File.Exists(Path.Combine(repoRoot.FullName, "archetypes", "catalog.json")))
        {
            repoRoot = repoRoot.Parent!;
        }

        repoRoot.ShouldNotBeNull("archetypes/catalog.json not found above the test bin dir");
        var seed = await File.ReadAllTextAsync(Path.Combine(repoRoot.FullName, "archetypes", "catalog.json"));

        (await SyncAsync(seed)).ShouldBe(CatalogSyncOutcome.Applied);

        await using var verify = fixture.CreateRegistryContext();
        var store = new CatalogStore(verify);
        var archetype = await store.FindArchetypeAsync("container-app-sql");
        archetype.ShouldNotBeNull();
        archetype.Status.ShouldBe(ArchetypeStatus.Active);
        (await store.ResolveNewestVersionAsync("container-app-sql"))!.Version.ShouldBe("v1.0.0");
    }
}
