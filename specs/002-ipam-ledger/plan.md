# Implementation Plan: IPAM Ledger

**Branch**: `002-ipam-ledger` | **Date**: 2026-06-15 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/002-ipam-ledger/spec.md`

## Summary

Stand up the platform's IP-address authority: deploy a private, Entra-only,
cheap-by-default control-plane Postgres database on the existing spec-001 CI rails, and
deliver the IPAM ledger schema + operations that live in it — regional `/16` supernets with a
reserved hub `/22` carve-out, and a concurrency-safe `allocate`/`release`/`register`/`query`
surface whose **non-overlap guarantee is enforced by the database itself** (a `btree_gist`
EXCLUDE constraint on `cidr`), with per-pool advisory-lock serialization. The schema and
allocator are authored and proven against a real Postgres (Testcontainers); the empty private
server is deployed with the `BTREE_GIST` extension allow-listed; applying the schema to the
live DB binds to the spec-006 runtime (the DB is private + Entra-only, so nothing reaches it
from CI yet). No verbs/CLI/MCP and no VNet creation here — those are specs 006 / 003 / 004.

## Technical Context

**Language/Version**: .NET 10 (LTS, pinned via `global.json`); OpenTofu 1.11.6 (pinned via
`.opentofu-version`).

**Primary Dependencies**: EF Core 10 + Npgsql (native `cidr` ↔ `System.Net.IPNetwork`),
`EFCore.NamingConventions` (snake_case); azurerm ~4.x + AVM modules
(`avm-res-dbforpostgresql-flexibleserver`, `avm-res-network-virtualnetwork`). **No Wolverine**
this spec (no messaging/outbox need yet); **no** MediatR/AutoMapper/Moq/Serilog/
FluentAssertions (prohibited).

**Storage**: Azure Database for PostgreSQL Flexible Server (Burstable `B_Standard_B1ms`,
32 GiB, VNet-injected, Entra-only, 30-day PITR, LRS). State for this stack:
`platform/control-plane` in the PDP backend.

**Testing**: xUnit + `Testcontainers.PostgreSql` + Respawn + Shouldly + NSubstitute. The GiST
exclusion constraint and advisory-lock allocator are validated against a real Postgres
(constitution: cannot be tested in-memory — FR-016).

**Target Platform**: Azure (platform subscription, East US 2); .NET library consumed by the
spec-006 control plane.

**Project Type**: Platform data layer (.NET class library + integration tests) plus an
OpenTofu infrastructure stack and a .NET CI workflow.

**Performance Goals**: `allocate` returns a committed, non-overlapping block in <1s under
normal load (SC-003); ≥100 concurrent same-region allocations yield zero overlaps/double-
allocations (SC-001).

**Constraints**: Private by default (no public endpoint); zero stored secrets (Entra-only);
non-overlap is absolute and database-enforced; everything destroyable with confirmation.

**Scale/Scope**: Personal platform — ~256 regions (`/16` each, index 0 reserved for
platform-shared infra), ~252 `/24` spokes per region; single-owner, internal consumers only.

## Constitution Check

*GATE: evaluated before Phase 0 and re-checked after Phase 1 design. Result: **PASS** — no
violations; Complexity Tracking empty.*

| Article | Gate | Status |
|---|---|---|
| I — Infra is Code | DB, VNet, DNS via OpenTofu/AVM; no portal mutations. | ✅ |
| II — AI calls verbs | No AI runtime / no runtime IaC generation in this spec. | ✅ N/A |
| III — Tagged/tracked | Control-plane RG + resources carry `pdp-*` tags; discoverable via Resource Graph. | ✅ |
| IV — Destroyable | Stack tears down cleanly; `controlplane-destroy` workflow + Article-IV protection on the DB (the ledger of record). | ✅ |
| V — AVM-first | AVM Postgres + VNet modules, pinned + smoke-tested under OpenTofu 1.11.6; any primitive carries a recorded justification. | ✅ |
| VI — No address without allocation | The control-plane VNet range is **registered in the ledger as a seeded reservation** (research §12), resolving the bootstrap chicken-and-egg without inventing space. | ✅ (see note) |
| VII — Hub owns egress | N/A — the control-plane VNet is not a spoke and defines no egress. | ✅ N/A |
| VIII — Plan before apply, confirm destroy | Rides plan-on-PR / apply-on-merge; destroy is `workflow_dispatch` with typed confirmation. | ✅ |
| IX — Secure & cheap | Private, Entra-only, Burstable B1ms, LRS, 30-day backup. | ✅ |
| X — Specs before code | Following specify → clarify → plan → tasks → implement. | ✅ |

**Additional constraints**: OpenTofu-only ✅; .NET 10 ✅; control vs execution plane
(OpenTofu runs only in CI, never in-process) ✅; no prohibited dependencies ✅; CAF naming
(`psql`/`vnet`/`snet`/`pep` already pinned in `docs/conventions.md`) ✅.

**Note (Article VI)** — the only subtle gate. Standing up the ledger requires a private DB,
which requires a VNet with a CIDR, which Article VI says must be registered in the ledger that
doesn't exist yet. Resolved exactly as spec-001 resolved the state-backend bootstrap: reserve
**region index 0 (`10.0.0.0/16`)** as a platform-shared supernet and **seed** the
control-plane VNet (`10.0.0.0/24`) as a pre-committed, non-allocatable reservation, so the
range is registered (Article VI holds) the moment the schema exists. This refines — does not
violate — the clarified addressing scheme (regions are `/16`s; index 0 is now reserved). The
refinement is recorded in [data-model.md](data-model.md) §1/§5 and flagged for owner awareness.

## Project Structure

### Documentation (this feature)

```text
specs/002-ipam-ledger/
├── plan.md              # This file
├── research.md          # Phase 0 — decisions (Azure/.NET, both MCP-verified) + architecture
├── data-model.md        # Phase 1 — region_pool, allocation, constraints, seed
├── quickstart.md        # Phase 1 — 7 validation scenarios mapped to SCs
├── contracts/
│   ├── ipam-operations.md    # register/allocate/release/query behavioral contract
│   └── control-plane-db.md   # IaC contract for the control-plane Postgres stack
└── tasks.md             # Phase 2 — created by /speckit-tasks (NOT here)
```

### Source Code (repository root)

```text
infra/control-plane/                 # NEW OpenTofu stack — state key platform/control-plane
├── versions.tf  backend.tf  variables.tf  outputs.tf  main.tf
├── README.md                        # AVM smoke-test note, protected-resource list, handoff
└── .terraform.lock.hcl

src/
└── Pdp.ControlPlane.Ipam/           # NEW .NET 10 class library (first platform code)
    ├── Entities/                    # RegionPool, Allocation, AllocationKind
    ├── IpamDbContext.cs             # snake_case, HasPostgresExtension("btree_gist")
    ├── Migrations/                  # initial schema + raw-SQL EXCLUDE constraint + seed
    ├── Allocator/                   # first-fit aligned free-block allocator
    └── IIpamLedger.cs + Ledger.cs   # RegisterRegion / Allocate / Release / Query

tests/
└── Pdp.ControlPlane.Ipam.Tests/     # NEW xUnit integration tests (Testcontainers + Respawn)
    ├── NonOverlapTests / ConcurrencyTests / AllocateReleaseTests
    ├── RegionRegistrationTests / QueryTests
    └── fixtures (PostgresFixture, migration apply, Respawn reset)

.github/workflows/
└── dotnet.yml                       # NEW — build + test on src/** , tests/**, *.sln changes

Pdp.sln                              # NEW solution at repo root
Directory.Build.props                # NEW shared analyzers/nullable/langversion
```

**Structure Decision**: A focused data-layer library + integration tests (not the full
control-plane host) is the smallest independently-testable slice that delivers the IP
authority; the spec-006 ASP.NET host will reference/extend `IpamDbContext`. The OpenTofu stack
and `.NET` CI workflow are additive on the spec-001 rails — no layout/convention/rail changes.
`docs/conventions.md` already pins the needed CAF abbreviations (`psql` is annotated "006";
this spec brings the control-plane DB forward to 002 — a one-line doc note, not a new
abbreviation).

## Complexity Tracking

> No constitution violations — table intentionally empty. The Article VI bootstrap is a
> documented refinement (seeded reservation), not a deviation; the migration-application
> boundary (research §13) defers no requirement, only the *timing* of applying the proven
> schema to the live private DB to spec 006 where a connecting runtime exists.
