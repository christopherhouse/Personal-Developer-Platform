# Phase 0 Research: IPAM Ledger

All Azure/.NET facts below were verified live via the Microsoft Learn and context7 MCP
servers (constitution rule: never answer version-sensitive Azure/.NET questions from
memory). Citations inline.

## 1. Private networking for the control-plane Postgres

**Decision**: Deploy PostgreSQL Flexible Server with **VNet integration (private access)** —
injected into a delegated subnet of a new control-plane VNet — **not** a private endpoint.
`public_network_access_enabled = false`; no public endpoint exists once injected.

**Rationale**: Microsoft recommends VNet injection for "no public endpoint reachable from
the internet" (Article IX, FR-002). It needs: a subnet delegated to
`Microsoft.DBforPostgreSQL/flexibleServers` (dedicated to the server), a Private DNS zone
whose name ends in `postgres.database.azure.com`, and a vnet-link from that zone to the
VNet. azurerm args: `delegated_subnet_id`, `private_dns_zone_id`,
`public_network_access_enabled = false`.

**Alternatives considered**: Private endpoint + public-access-disabled — rejected; VNet
injection is the documented fit for hub-and-spoke and the platform will vnet-integrate the
ACA control plane into the same VNet in spec 006. Public access — rejected outright
(Article IX).

**Caveat carried to implementation**: the delegated subnet cannot be resized once the
server exists, and the server cannot be moved to another subnet/VNet later — size the
subnet correctly up front (a `/28` delegated subnet is ample for one Flexible Server).

*Source*: learn.microsoft.com/azure/postgresql/network/concepts-networking-private;
templates/microsoft.dbforpostgresql 2026-01-01-preview.

## 2. Entra-only authentication, zero stored secrets

**Decision**: On `azurerm_postgresql_flexible_server`, set the `authentication` block to
`active_directory_auth_enabled = true` + `password_auth_enabled = false` ("Microsoft Entra
authentication only"). No `administrator_login`/`administrator_password` → zero stored
secrets (FR-003). Set the owner as Entra admin via
`azurerm_postgresql_flexible_server_active_directory_administrator`
(`tenant_id`, `object_id`, `principal_name`, `principal_type = "User"`). The control-plane
managed identity is added as an Entra principal in spec 006 when a runtime exists.

**Rationale**: Mirrors the spec-001 no-stored-secrets posture across the data plane.
**Alternatives**: password auth / mixed mode — rejected (would store a secret).

*Source*: learn.microsoft.com/azure/postgresql/security/security-reset-admin-password;
templates/microsoft.dbforpostgresql 2024-08-01 (authConfig).

## 3. `btree_gist` extension (the non-overlap constraint)

**Decision**: Allow-list the extension via the `azure.extensions` server parameter
(`azurerm_postgresql_flexible_server_configuration`, `name = "azure.extensions"`,
`value = "BTREE_GIST"`). Confirmed supported on all current PG majors (PG16/17 →
btree_gist 1.7). `azure.extensions` is dynamic (no restart for the allow-list).

**Rationale**: A GiST EXCLUDE constraint mixing `pool_id WITH =` and `network WITH &&`
requires `btree_gist` to bring B-tree equality into a GiST index. This is the load-bearing
guarantee (FR-006).

*Source*: learn.microsoft.com/azure/postgresql/extensions/concepts-extensions-by-engine,
.../concepts-extensions-versions, .../how-to-allow-extensions.

## 4. Backup / recovery (30-day window — clarify Q5)

**Decision**: `backup_retention_days = 30` (valid range 7–35), `geo_redundant_backup_enabled
= false` (LRS, cheap by default; with HA off this is locally-redundant). Both are
create-time settings. Satisfies FR-014.

*Source*: learn.microsoft.com/azure/postgresql/backup-restore/concepts-backup-restore.

## 5. Smallest viable SKU (Article IX)

**Decision**: `sku_name = "B_Standard_B1ms"` (Burstable, 1 vCore / 2 GiB), `storage_mb =
32768` (32 GiB minimum). Cheap; easy to recreate.

*Source*: learn.microsoft.com/azure/postgresql/compute-storage/concepts-compute.

## 6. AVM modules (Article V — AVM-first)

**Decision**: Use `Azure/avm-res-dbforpostgresql-flexibleserver/azurerm` (pin exact;
**v0.2.2** was latest at research time — re-confirm the registry's latest and smoke-test
under OpenTofu 1.11.6 before adoption, per Article V). Its inputs cover everything above:
`sku_name`, `storage_mb`, `delegated_subnet_id`, `private_dns_zone_id`,
`public_network_access_enabled`, `active_directory_auth_enabled`, `password_auth_enabled`,
`ad_administrator`, `backup_retention_days`, `geo_redundant_backup_enabled`,
`server_configuration` (set `azure.extensions = "BTREE_GIST"` here). VNet + delegated
subnet via `Azure/avm-res-network-virtualnetwork/azurerm`; Private DNS zone + link via
`Azure/avm-res-network-privatednszone/azurerm` (or azurerm primitives where AVM is
heavier than the single resource warrants — record the justification per Article V).

**Smoke-test task** required before relying on the module (a `tofu init/plan/validate`,
ideally apply/destroy in a scratch RG), recorded in `infra/control-plane/README.md`.

*Source*: registry.terraform.io + github.com/Azure/terraform-azurerm-avm-res-dbforpostgresql-flexibleserver.

## 7. `cidr` ↔ .NET mapping

**Decision**: Model IP ranges as **`System.Net.IPNetwork`** (the BCL type, .NET 8+). Npgsql
10 maps `cidr` → `IPNetwork` **by default**; the old `NpgsqlTypes.NpgsqlCidr` is obsolete.
Declare the column explicitly: `[Column(TypeName = "cidr")]` / `.HasColumnType("cidr")`.
`IPNetwork` requires host bits zero for the prefix — exactly `cidr` semantics.

*Source*: npgsql.org/doc/release-notes/10.0.html; npgsql.org/doc/api/NpgsqlTypes.NpgsqlCidr.

## 8. GiST exclusion constraint via EF Core migrations

**Decision**: EF Core cannot model EXCLUDE constraints declaratively. Declare the extension
in the model (`modelBuilder.HasPostgresExtension("btree_gist")` → the provider emits
`CREATE EXTENSION` in the migration) and add the constraint as raw SQL in the migration:

```sql
ALTER TABLE allocations
  ADD CONSTRAINT allocations_no_overlap
  EXCLUDE USING gist (pool_id WITH =, network inet_ops WITH &&);
```

The `inet_ops` operator class is required for `cidr`/`inet` in a GiST exclusion, paired with
`&&` (overlaps). `Down` drops the constraint. Snake_case identifiers (see §11) are written
literally in the raw SQL.

*Source*: github.com/npgsql/efcore.pg (NpgsqlModelBuilderExtensions, metadata docs);
PostgreSQL network-type operator classes.

## 9. Concurrency-safe allocator (advisory locks)

**Decision**: Serialize allocation per pool with the **transaction-scoped** advisory lock
`pg_advisory_xact_lock(key)`, called via
`context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({key})")`
inside an explicit `BeginTransactionAsync`. Xact-scoped = auto-release at COMMIT/ROLLBACK
(no leak); session locks are fragile under pooling and are avoided. The lock `key` is a
stable `bigint` derived from the pool identity, so different pools allocate in parallel
while same-pool requests serialize (FR-008). The GiST exclusion constraint (§8) is the
ultimate backstop even if a lock is bypassed.

**Allocator algorithm**: within the lock, **first-fit lowest aligned free block** — scan
candidate blocks of the requested prefix size, in ascending address order, within
`pool − (hub carve-out + existing allocations + reserved blocks)`, return the first block
that is properly aligned to its own prefix and overlaps nothing; INSERT it. No free block →
clean `PoolExhausted` failure, nothing recorded (FR-007). Implemented in C# for clarity and
testability; deterministic given pool state.

*Source*: learn.microsoft.com/dotnet/api/...executesqlinterpolatedasync (efcore-10.0);
PostgreSQL advisory-lock functions.

## 10. Testing against a real Postgres (FR-016)

**Decision**: `Testcontainers.PostgreSql` (`PostgreSqlBuilder`, pinned image e.g.
`postgres:17-alpine`, `IAsyncLifetime`, `GetConnectionString()`) for a throwaway real
Postgres per test run; **Respawn** (`DbAdapter.Postgres`, include `public`) to reset data
between tests while keeping schema + extensions. xUnit + Shouldly + NSubstitute. Migrations
(incl. `btree_gist` + exclusion constraint) are applied to the container, so the GiST
guarantee and advisory-lock serialization are exercised against real engine behavior — the
constitution's reason Testcontainers is mandatory (in-memory can't enforce GiST).

*Source*: github.com/testcontainers/testcontainers-dotnet; Respawn docs.

## 11. snake_case naming

**Decision**: `optionsBuilder.UseNpgsql(...).UseSnakeCaseNamingConvention()`
(`EFCore.NamingConventions`). Tables/columns become snake_case; raw-SQL migrations reference
the snake_case identifiers literally.

*Source*: github.com/efcore/efcore.namingconventions.

---

## 12. Architectural decision — the control-plane VNet and Article VI bootstrap

**Problem**: FR-002 requires the DB be private, which requires a control-plane VNet with a
CIDR. Article VI forbids any VNet with an unregistered range — but the IPAM ledger that
registers ranges is exactly what this spec is standing up. Chicken-and-egg, mirroring
spec-001's state-backend bootstrap.

**Decision**: Reserve **region index 0 (`10.0.0.0/16`) as a non-geographic, platform-shared
supernet** for control-plane/shared infrastructure; geographic regions occupy indices
1–255. The control-plane VNet takes a fixed, documented block from it
(**`10.0.0.0/24`**, server delegated subnet `10.0.0.0/28`). The IPAM **seed migration**
records the platform supernet and this reservation as a pre-committed, non-allocatable
entry, so the ledger is self-consistent and Article VI holds: the platform's own VNet range
**is** registered — as a seeded reservation rather than a dynamic allocation. The fixed
CIDR is a documented constant shared by the IaC (`infra/control-plane/`) and the seed,
exactly as spec-001 treated the seed-backend identifiers.

**Rationale**: Keeps every PDP VNet inside IPAM-governed space with database-enforced
non-overlap, while solving the ordering problem the same way spec-001 did (anchor the
bootstrap outside/ahead of the system it creates). This **refines** the clarified scheme
(regions are `/16`s) by reserving index 0 — flagged for owner awareness.

**Alternative**: put the control-plane VNet outside `10.0.0.0/8` (e.g. `192.168.0.0/24`) and
treat it as un-IPAM'd bootstrap infra — rejected: Article VI is absolute ("no VNet with an
unregistered range").

## 13. Architectural decision — where migrations get applied (scope boundary)

**Decision**: This spec **authors and verifies** the schema + migrations + allocator against
Testcontainers (a real Postgres), and **deploys the empty private DB** with the
`azure.extensions = BTREE_GIST` allow-list in place so the migration will succeed later.
**Applying the schema to the live Azure DB is deferred to spec 006**, when a runtime exists
inside the VNet to connect (the ACA control plane runs `Database.Migrate()` on startup, or a
dispatched in-VNet job). The live DB is private and Entra-only, so nothing in this spec can
(or should) reach it from CI.

**Rationale**: Honest to the private-by-default constraint and the spec's own scope note
("operations exercised directly and by tests against the database; verbs/runtime are spec
006"). FR-016's "real database" acceptance is met by Testcontainers; US1's "DB exists"
acceptance is met by the deployed server. **Flagged for owner awareness** — the production
ledger becomes *operable* in spec 006, not here.

**Alternative**: stand up a self-hosted/in-VNet CI runner now to apply migrations —
rejected as premature infrastructure for no current consumer.

## 14. Project structure & CI

**Decision**: First .NET code lands as a focused library + integration tests, plus a new
OpenTofu stack and a .NET CI workflow:

- `infra/control-plane/` — OpenTofu stack (VNet, delegated subnet, private DNS zone + link,
  Postgres Flexible Server via AVM, `azure.extensions` config, Entra admin), state key
  `platform/control-plane`. Rides the existing plan-on-PR / apply-on-merge rails.
- `src/Pdp.ControlPlane.Ipam/` — .NET 10 class library: entities (`RegionPool`,
  `Allocation`), `IpamDbContext` (snake_case, `btree_gist`), EF Core migrations (incl. the
  raw-SQL exclusion constraint + seed), the allocator, and the ledger operation interface
  (`RegisterRegion`, `Allocate`, `Release`, `Query`). Spec 006 hosts/extends this DbContext.
- `tests/Pdp.ControlPlane.Ipam.Tests/` — xUnit integration tests (Testcontainers + Respawn):
  non-overlap, concurrency, allocate/release/idempotency, region registration, queries.
- `Pdp.sln` + `Directory.Build.props` (shared analyzers, nullable, langversion) at repo root.
- `.github/workflows/dotnet.yml` — `dotnet build` + `dotnet test` (Docker-backed
  Testcontainers) on PRs touching `src/**`, `tests/**`, or the solution; matches the
  `global.json` SDK pin. Required check on `main` alongside the IaC checks.

**Rationale**: Smallest slice that delivers the data authority and is independently testable;
defers spec-006 concerns (ASP.NET host, Wolverine, verbs) without blocking them. Wolverine is
**not** introduced here (no messaging/outbox need yet) — avoids premature dependency.
