# Tasks: IPAM Ledger

**Input**: Design documents from `/specs/002-ipam-ledger/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/, quickstart.md

**Tests**: Test tasks **are** included. FR-016 and the constitution require the GiST
exclusion constraint and the advisory-lock allocator to be validated against a **real**
Postgres (Testcontainers) — the tests are the acceptance mechanism for US2–US4, not optional
extras. Write each story's tests first and let them fail before implementing.

**Organization**: Tasks grouped by user story (US1 infra, US2 allocation authority, US3
regional scheme, US4 visibility) so each is an independently testable increment.

## Key facts carried from design (do not re-derive)

| Fact | Value |
|---|---|
| Addressing | `10.0.0.0/8`; `/16` per region (2nd octet = index); **index 0 = platform-shared** |
| Hub carve-out | `/22` at top of each `/16` (`10.R.252.0/22`), non-allocatable |
| Spoke prefixes | default `/24`, permitted `/29`–`/22` |
| Control-plane VNet | seeded reservation `10.0.0.0/24` (delegated subnet `10.0.0.0/28`) |
| Non-overlap | `EXCLUDE USING gist (pool_id WITH =, network inet_ops WITH &&)` + `btree_gist` |
| Concurrency | `pg_advisory_xact_lock(pool)`, transaction-scoped |
| `cidr` ↔ .NET | `System.Net.IPNetwork`, `[Column(TypeName="cidr")]` (Npgsql 10 default) |
| DB | `psql-pdp-eastus2-controlplane`, `B_Standard_B1ms`, 32 GiB, private/Entra-only, 30-day PITR, LRS, `azure.extensions=BTREE_GIST` |
| State key | `platform/control-plane` |

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependencies)
- **[Story]**: US1–US4 per spec.md

---

## Phase 1: Setup (shared infrastructure)

**Purpose**: The .NET solution skeleton, the OpenTofu stack scaffold, and the .NET CI
workflow everything else lands on. (`global.json` already pins the .NET SDK — spec 001.)

- [X] T001 Create the .NET solution `Pdp.sln` at repo root and `Directory.Build.props`
      (nullable enable, implicit usings, `LangVersion latest`, analyzers on, warnings-as-errors)
- [X] T002 [P] Create class library `src/Pdp.ControlPlane.Ipam/Pdp.ControlPlane.Ipam.csproj`
      (`net10.0`) with package refs: `Npgsql.EntityFrameworkCore.PostgreSQL`,
      `EFCore.NamingConventions`, `Microsoft.EntityFrameworkCore.Design`; add to `Pdp.sln`
- [X] T003 [P] Create test project
      `tests/Pdp.ControlPlane.Ipam.Tests/Pdp.ControlPlane.Ipam.Tests.csproj` (xUnit +
      `Testcontainers.PostgreSql`, `Respawn`, `Shouldly`, `NSubstitute`,
      `Microsoft.EntityFrameworkCore`) referencing the library; add to `Pdp.sln`
- [X] T004 Scaffold the `infra/control-plane/` OpenTofu stack: `versions.tf`
      (`required_version "~> 1.11.0"`, `azurerm "~> 4.77.0"`, `storage_use_azuread = true`,
      `features {}`), `backend.tf` (PDP backend, key `platform/control-plane`,
      `use_azuread_auth = true`), `variables.tf`, `outputs.tf` (stubs that keep
      `tofu validate` green until T011)
- [X] T005 [P] Create `.github/workflows/dotnet.yml`: `dotnet build` + `dotnet test` on PRs
      touching `src/**`, `tests/**`, or `*.sln`; SDK from `global.json`; Docker available for
      Testcontainers; runs on Node-free .NET runner

**Checkpoint**: Solution builds (empty), stack validates, CI workflows exist.

---

## Phase 2: Foundational (blocking prerequisites)

**⚠️ CRITICAL**: The schema and test harness block all data-layer stories (US2–US4).

- [X] T006 [P] Define entities in `src/Pdp.ControlPlane.Ipam/Entities/`: `RegionPool`
      (id, region, region_index, supernet, hub_carveout, created_at), `Allocation`
      (id, pool_id, name, network, prefix_length, kind, allocated_at), `AllocationKind` enum
      (`Spoke`, `Reservation`) per data-model.md §2–3
- [X] T007 Implement `src/Pdp.ControlPlane.Ipam/IpamDbContext.cs`: `UseSnakeCaseNamingConvention`,
      `HasPostgresExtension("btree_gist")`, `cidr` mappings (`IPNetwork` + `HasColumnType("cidr")`),
      `UNIQUE (pool_id, name)` on `allocation` (depends on T006)
- [X] T008 Create the initial EF Core migration in `src/Pdp.ControlPlane.Ipam/Migrations/`:
      both tables; raw-SQL `EXCLUDE USING gist` on `allocation` (`pool_id WITH =, network
      inet_ops WITH &&`) and on `region_pool.supernet`; seed the platform pool
      (`region='platform'`, index 0, `10.0.0.0/16`) and the `control-plane-vnet` reservation
      (`10.0.0.0/24`, kind `Reservation`) per data-model.md §5 (depends on T007)
- [X] T009 Implement the test harness in `tests/Pdp.ControlPlane.Ipam.Tests/`: `PostgresFixture`
      (`PostgreSqlBuilder` pinned image e.g. `postgres:17-alpine`, `IAsyncLifetime`, apply
      migrations on init, expose connection string), Respawn reset (`DbAdapter.Postgres`,
      include `public`) between tests, and a pool-seeding helper (depends on T007, T008)

**Checkpoint**: A throwaway real Postgres comes up with the full schema (extension +
exclusion constraints + seed); stories can now be built and tested.

---

## Phase 3: User Story 1 — Control-plane database deployed, private & on the rails (P1) 🎯 MVP

**Goal**: A private, Entra-only, cheap control-plane Postgres exists on the spec-001 rails,
conformant and destroyable. The hardest infra prerequisite; the live ledger home.

**Independent Test**: quickstart Scenario 1 — PR→plan→merge→apply; DB has no public endpoint,
zero stored secrets, conformant tags/naming, 30-day backup, `BTREE_GIST` allow-listed;
`controlplane-destroy` is blocked by protection (SC-006, SC-007).

- [ ] T010 [US1] Smoke-validate `Azure/avm-res-dbforpostgresql-flexibleserver/azurerm` (pinned
      exact) and `Azure/avm-res-network-virtualnetwork/azurerm` under OpenTofu 1.11.6
      (init/plan/validate; ideally apply/destroy in a scratch RG); record the result + any
      fallback in `infra/control-plane/README.md` (Article V; re-confirm latest pin at run)
- [ ] T011 [US1] Implement `infra/control-plane/main.tf`: RG `rg-pdp-eastus2-controlplane`
      (universal tags), VNet `vnet-pdp-eastus2-controlplane` `10.0.0.0/24`, delegated subnet
      `snet-pdp-eastus2-cp-postgres` `10.0.0.0/28` (delegation
      `Microsoft.DBforPostgreSQL/flexibleServers`), private DNS zone
      `…private.postgres.database.azure.com` + vnet-link, Postgres
      `psql-pdp-eastus2-controlplane` via AVM (`B_Standard_B1ms`, `storage_mb=32768`,
      `public_network_access_enabled=false`, `active_directory_auth_enabled=true`,
      `password_auth_enabled=false`, owner Entra admin, `backup_retention_days=30`,
      `geo_redundant_backup_enabled=false`, server config `azure.extensions="BTREE_GIST"`);
      populate `outputs.tf` (server name/FQDN, vnet/subnet ids) (depends on T004, T010)
- [ ] T012 [US1] Add the Article-IV carve-out in `infra/control-plane/main.tf`: `CanNotDelete`
      management lock on the RG and `prevent_destroy` on the server; enumerate the protected
      set in `infra/control-plane/README.md` (FR-015)
- [ ] T013 [US1] Extend `.github/workflows/iac-plan.yml` and `iac-apply.yml` to include the
      `control-plane` stack (fmt/validate/plan on PR; re-plan + apply on merge), matching the
      foundations stack's job/matrix shape and concurrency group
- [ ] T014 [P] [US1] Create `.github/workflows/controlplane-destroy.yml`: `workflow_dispatch`
      with required `destroy-confirm` input matching `control-plane`, `tofu plan -destroy`
      then destroy, `concurrency: tofu-control-plane` (Article VIII typed confirmation)
- [ ] T015 [US1] Write `infra/control-plane/README.md`: stack purpose, AVM smoke note,
      protected-resource enumeration, the spec-006 migration + managed-identity handoff
      (research.md §13), state key, teardown consequence (FR-015)
- [ ] T016 [P] [US1] Commit `infra/control-plane/.terraform.lock.hcl` (FR-006 pin mechanics)
- [ ] T017 [US1] Validate quickstart Scenario 1: PR→plan→merge→apply; verify no public
      endpoint, Entra-only + zero DB secrets (SC-006), tags/naming, `backup_retention_days=30`,
      `BTREE_GIST` allow-listed; run the `controlplane-destroy` protection drill (SC-007)

**Checkpoint**: The live control-plane DB exists and is protected — MVP delivered.

---

## Phase 4: User Story 2 — Address space allocated without ever overlapping (P1)

**Goal**: Concurrency-safe `allocate`/`release` whose non-overlap is enforced by the database.
The reason the spec exists. Runs against Testcontainers — independent of US1's deployment.

**Independent Test**: quickstart Scenarios 2–4 — non-overlap rejected by the DB; ≥100
concurrent allocations distinct & non-overlapping; allocate <1s + idempotent on name; release
reclaims (SC-001, SC-002, SC-003, SC-004).

- [ ] T018 [P] [US2] Integration test `tests/Pdp.ControlPlane.Ipam.Tests/NonOverlapTests.cs`:
      seed a pool, allocate blocks, assert a deliberate overlapping insert is rejected by
      `allocations_no_overlap` (write first — must fail)
- [ ] T019 [P] [US2] Integration test `…/ConcurrencyTests.cs`: ≥100 concurrent `allocate`
      calls on one pool yield distinct, non-overlapping blocks (or clean `PoolExhausted`),
      zero double-allocation (write first — must fail)
- [ ] T020 [P] [US2] Integration test `…/AllocateReleaseTests.cs`: allocate returns a valid
      block durably; repeat with same name → same block; name-conflict (different size)
      rejected; allocate against an **unregistered region → `RegionNotRegistered`** (FR-012);
      release reclaims space; release-unknown is a clean no-op; **releasing a reservation
      (e.g. `control-plane-vnet`) → `CannotReleaseReservation`** (contract) (write first — fail)
- [ ] T021 [US2] Implement the allocator in `src/Pdp.ControlPlane.Ipam/Allocator/` —
      first-fit lowest aligned free block within `supernet − (hub_carveout + existing
      allocations)`, respecting requested-prefix alignment; deterministic (depends on T006)
- [ ] T022 [US2] Implement `Allocate` and `Release` in `src/Pdp.ControlPlane.Ipam/Ledger.cs`
      (`IIpamLedger`): `BeginTransactionAsync` + `pg_advisory_xact_lock(pool)` via
      `ExecuteSqlInterpolatedAsync`, prefix validation (`/29`–`/22`), idempotent on
      `(pool,name)`, `PoolExhausted`/`AllocationNameConflict`; release = idempotent delete,
      refuse `Reservation` kind (depends on T021, T009)
- [ ] T023 [US2] Make T018–T020 pass against Testcontainers; confirm SC-001/SC-002/SC-003/SC-004

**Checkpoint**: The non-overlap authority is proven against a real Postgres.

---

## Phase 5: User Story 3 — Regional scheme with reserved hub carve-out (P2)

**Goal**: `register_region` derives the `/16` supernet and reserves the `/22` hub carve-out;
overlapping regions are refused; spokes draw only from the remainder.

**Independent Test**: quickstart Scenario 5 — register records `10.N.0.0/16` + reserves
`10.N.252.0/22`; carve-out never allocated to a spoke; overlapping region registration refused.

- [ ] T024 [P] [US3] Integration test `…/RegionRegistrationTests.cs`: register derives
      supernet+carve-out, reserves the carve-out non-allocatable, allocates spokes only from
      the remainder, and refuses an overlapping-supernet region (write first — must fail)
- [ ] T025 [US3] Implement `RegisterRegion` in `Ledger.cs`: derive `supernet=10.N.0.0/16` and
      `hub_carveout=10.N.252.0/22` from `region_index`, validate index 1–255 (0 reserved),
      reserve the carve-out, idempotent re-register, surface `RegionAlreadyExists` /
      `SupernetOverlap` (the latter from the DB exclusion constraint) (depends on T009)
- [ ] T026 [US3] Ensure the allocator (T021) excludes the pool's `hub_carveout` from
      allocatable space; make T024 pass; confirm US3 acceptance scenarios

**Checkpoint**: The addressing scheme is concrete; specs 003/004 can register regions.

---

## Phase 6: User Story 4 — Allocation visibility per region (P3)

**Goal**: Query what's allocated, to whom, and free per region.

**Independent Test**: quickstart Scenario 6 — `query(region)` reports supernet, carve-out,
live allocations (name/size/kind), free space; released not shown; `query_all` includes the
platform pool + reservation (SC-005).

- [ ] T027 [P] [US4] Integration test `…/QueryTests.cs`: `query` reports supernet, carve-out,
      live allocations and free space; a released block disappears; `query_all` includes the
      platform pool + `control-plane-vnet` reservation (write first — must fail)
- [ ] T028 [US4] Implement `Query`/`QueryAll` in `Ledger.cs`: read-only `RegionView`
      projection including free-space computation (pool − carve-out − live allocations); make
      T027 pass (depends on T009)

**Checkpoint**: All four stories independently functional and tested.

---

## Phase 7: Polish & Cross-Cutting

- [ ] T029 [P] Update `scripts/setup-branch-protection.ps1` to also require the `dotnet`
      check and the control-plane `plan` check on `main`; run it (idempotent)
- [ ] T030 [P] Update `docs/conventions.md`: note the control-plane DB (`psql`) arrives in
      spec **002** (abbreviation already pinned, "used-by" was 006); confirm the postgres
      private-DNS-zone example is present
- [ ] T031 Run the full quickstart (Scenarios 2–7) via `dotnet test`; confirm
      SC-001…SC-005 and SC-008 (operations cover specs 003/004 with no schema/scheme change);
      record results in a Phase-7 note

---

## Dependencies & Execution Order

- **Setup (Phase 1)** → **Foundational (Phase 2)** → user stories.
- **US1 (Phase 3)** is **independent** of US2–US4: it deploys Azure infra and needs only the
  Phase 1 stack scaffold (T004) + AVM smoke-test (T010). It does **not** need the .NET schema.
- **US2–US4** need Foundational (T006–T009, the schema + test harness) but **not** US1's
  deployment (they run against Testcontainers).
- **US2 ↔ US3**: US2 tests seed pools via the fixture helper (T009), so US2 does **not**
  depend on US3's `RegisterRegion`. US3 adds the real region semantics; T026 wires the
  carve-out exclusion into the allocator from T021.
- **Polish (Phase 7)**: T029 after T005+T013; T031 after US2–US4.

### Parallel opportunities

- Phase 1: T002, T003 after T001; T005 alongside T004.
- Phase 2: T006 then T007→T008→T009 (sequential — same schema chain).
- US1: T014, T016 alongside T011/T015; T010 can start as soon as T004 exists.
- US2: T018, T019, T020 together (distinct test files) before T021/T022.
- US1 (infra) and US2 (data layer) can be built fully in parallel by different efforts.

---

## Implementation Strategy

**MVP = Phase 1 + 2 + User Story 1**: the private control-plane DB deployed and protected on
the rails — the hardest infrastructure prerequisite. Stop and validate Scenario 1.

**Co-critical (P1) US2** can proceed in parallel with US1 since it's Testcontainers-based; it
delivers the actual non-overlap authority. Together US1+US2 are the substantive increment.

**Increment 2**: US3 (regional scheme + carve-out) then US4 (visibility) — each a thin
addition on the same schema, independently tested.

Commit after each task or logical group; all `infra/**`, `src/**`, and `tests/**` changes ride
PR → plan/test → merge (the spec-001 rails enforce themselves).
