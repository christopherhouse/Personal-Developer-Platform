# Implementation Plan: Environment Inventory

**Branch**: `005-environment-inventory` | **Date**: 2026-06-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/005-environment-inventory/spec.md`

## Summary

Build PDP's live, read-only **inventory**: the authoritative answer to "what does PDP manage, and
where?" computed **solely** from Azure Resource Graph (ARG) over the mandatory `pdp-*` tag schema,
across every subscription the read identity can see (constitution Article III; glossary *Inventory*).
It discovers subscriptions at runtime, queries ARG for resource groups tagged `pdp-managed == 'true'`,
classifies each into the taxonomy (**fabric** / **spoke** / **workload**, workloads grouped into
**environments**), reports each item with its subscription and region, answers the three headline
questions, returns a **typed structured** result, and surfaces **tag-side drift** (orphan,
conformance, invisible, ambiguous) — all without touching local records, the IPAM ledger, or
OpenTofu state.

**Deliverable (Clarifications 2026-06-17)**: a reusable .NET inventory **component**
(`Pdp.ControlPlane.Inventory`) consumed later by the spec 006/007 verb/CLI/MCP layers, **plus** a
thin **demonstrable console harness** (`Pdp.Inventory.Demo`) that proves the success criteria live
against the deployed `westus3` fabric and the spec-004 spokes. This is the **first .NET feature** in
the platform after the spec-002 IPAM library; it mirrors that project's `Pdp.ControlPlane.*` shape.

**Footprint**: read-only. The spec creates **no Azure resources and provisions no identity/RBAC** —
the component takes a **pluggable `TokenCredential`**, the harness runs under the owner's local
credential (read-only ARG queries are permitted locally), and the spec-006 control-plane identity
injects its own credential later with no source change. Nothing to tear down (SC-009).

## Technical Context

**Language/Version**: .NET 10 (LTS), C# `latest` — pinned via `global.json` (`10.0.100`) and
`Directory.Build.props` (`net10.0`, nullable on, analyzers + warnings-as-errors). No Python; no new
language.

**Primary Dependencies**:
- `Azure.ResourceManager` (4.x line, currently v1.14.x package) — subscription discovery via
  `ArmClient.GetSubscriptions()`; `ArmClient(TokenCredential, …)` is the pluggable-credential seam.
- `Azure.ResourceManager.ResourceGraph` (v1.1.0 stable) — the ARG query surface
  (`TenantResource.GetResources(ResourceQueryContent)`), paged via
  `ResourceQueryRequestOptions.SkipToken` ↔ `ResourceQueryResult.SkipToken` (1000-record page cap;
  `allowPartialScopes` for coverage honesty).
- `Azure.Identity` — `DefaultAzureCredential` for the demo harness; the component itself only
  depends on `Azure.Core.TokenCredential` (injected).
- `System.Text.Json` (BCL) — the machine-readable rendering of the structured result.
- **No** EF Core / Npgsql / Postgres / Wolverine — the environment registry is spec 006; this spec
  is pure ARG reads. **No** OpenTofu, `azurerm`, AVM. **No** prohibited deps.

**Storage**: **None.** No database, no OpenTofu state, no cache. Inventory is recomputed live from
ARG per query (Article III). The owner-managed seed backend (`RG-TF`/`cmhtfstatesa`) is explicitly
excluded.

**Testing**: xUnit + Shouldly (assertions) + NSubstitute (fakes). The classification, grouping, and
drift logic is a **pure engine** over an injected `IResourceGraphReader` abstraction, so it is fully
unit-tested with fabricated ARG rows — **no Azure, no Testcontainers** (no DB in this spec). The
live success criteria (SC-001…SC-008) are proven by the quickstart running the demo harness against
the real platform, mirroring how spec 004 verified live.

**Target Platform**: A .NET class library (`netstandard`-compatible `net10.0`) usable both (a)
locally under the owner's credential via the demo harness and (b) later in-process in the spec-006
control plane on Azure Container Apps under its managed identity. Reads span the platform
subscription and all accessible target subscriptions (e.g., `chhouse-1`).

**Project Type**: .NET solution additions only — one class library, one thin console harness, one
xUnit test project, wired into the existing `Pdp.sln`. **No** OpenTofu stack, **no** Azure
resources, **no** GitHub `workflow_dispatch` (nothing is deployed; the execution plane is not
involved because there is no mutation).

**Performance Goals**: SC-005 — full sweep across all accessible subscriptions **P95 < 5 s**; a
scoped query (environment X) **< 2 s**. ARG is the fast path; pagination streams at 1000 rows/page;
subscription discovery is one ARM call.

**Constraints**: read-only (no mutation, no RBAC provisioned — FR-017/FR-019); ARG is the **sole**
source (Article III; FR-002); RG-granularity only (FR-023); drift is informational, inventory always
succeeds (FR-016); least-privilege read credential; private/cheap by default (no endpoints, no
resources — Article IX).

**Scale/Scope**: all subscriptions the identity can enumerate (ARG accepts up to 1000 subscription
scopes per query — batch if ever exceeded; `allowPartialScopes` keeps coverage honest); tens–hundreds
of managed resource groups across multiple subscriptions and regions.

## Constitution Check

*GATE: evaluated before Phase 0; re-checked after Phase 1 design. Result: **PASS** — a read-only,
ARG-backed component is the direct embodiment of Article III; no article requires a deviation.*

| Article | Gate | Status |
|---|---|---|
| I — Infra is Code | No infrastructure is created or mutated; the spec adds .NET code only. No portal/`az` mutations. | ✅ N/A (read-only) |
| II — AI calls verbs / plane split | Inventory is a **control-plane read function**; this spec ships the component the spec-006 verbs will wrap. No IaC generated/applied at runtime. | ✅ |
| III — Tagged/tracked, ARG-sourced | **This spec is the embodiment of Article III**: inventory derived solely from Resource Graph over the `pdp-*` tag schema, never local records; untagged/mis-tagged surfaced as drift, not dropped. | ✅ (central) |
| IV — Destroyable | Nothing is created in Azure ⇒ nothing to tear down; SC-009 states this and FR-019 provisions no RBAC. | ✅ (vacuous) |
| V — AVM-first | No IaC in this spec. | ✅ N/A |
| VI — No address without allocation | No address space touched; the IPAM ledger is read by spec 006, not here. | ✅ N/A |
| VII — Hub owns egress | No networking. | ✅ N/A |
| VIII — Plan before apply / confirm destroy | No mutations ⇒ no plan/apply/destroy. Inventory never mutates. | ✅ N/A |
| IX — Secure & cheap | No public endpoints, no provisioned resources; **least-privilege read-only** credential, owner-local for the demo; zero standing cost. | ✅ |
| X — Specs before code | specify → clarify → plan → tasks → implement, followed. | ✅ |

**Additional constraints**: .NET 10 pinned ✅; **no prohibited deps** (only Azure SDK + xUnit /
Shouldly / NSubstitute) ✅; **control vs execution plane** preserved — inventory is a control-plane
read, no execution-plane dispatch needed ✅; **division of truth** honored — ARG is the truth for
*what's deployed* (this spec); Postgres records *intent* (deferred to spec 006), and this spec reads
neither the ledger nor the registry ✅; **.NET naming** mirrors `Pdp.ControlPlane.Ipam`
(`Pdp.ControlPlane.Inventory`) ✅. **Glossary**: *Inventory* already defined; the drift-category
vocabulary (orphan / conformance / invisible / ambiguous) is spec-local and documented in
`data-model.md` — research §5 notes adding a **"Drift"** glossary row for platform-wide reuse.

## Project Structure

### Documentation (this feature)

```text
specs/005-environment-inventory/
├── plan.md              # This file
├── research.md          # Phase 0 — ARG/SDK surface, query design, classification & drift rules, credential model
├── data-model.md        # Phase 1 — the typed domain model + classification/drift decision tables
├── quickstart.md        # Phase 1 — live validation scenarios mapped to SC-001…SC-009
├── contracts/
│   └── inventory-interfaces.md   # the component's read surface + the ARG-reader seam
├── checklists/requirements.md
└── tasks.md             # Phase 2 — /speckit-tasks (NOT created here)
```

### Source Code (repository root)

```text
src/Pdp.ControlPlane.Inventory/                 # NEW class library — the reusable component
├── Pdp.ControlPlane.Inventory.csproj           #   refs Azure.ResourceManager(.ResourceGraph); no DB
├── IInventoryService.cs                         #   read surface: Snapshot(), Environments(), Spokes(), Environment(name)
├── InventoryService.cs                          #   orchestrates: discover → query → classify → group → drift
├── IResourceGraphReader.cs                      #   testability seam over ARG (returns typed rows)
├── ResourceGraphReader.cs                       #   adapter over Azure.ResourceManager.ResourceGraph (+ subscription discovery)
├── Queries/InventoryQueries.cs                  #   the KQL strings (managed RGs; looks-managed-but-untagged)
├── Classification/
│   ├── TagSchema.cs                             #   pdp-* tag names + value rules from docs/conventions.md §2
│   ├── ResourceGroupClassifier.cs               #   row → fabric|spoke|workload|orphan|ambiguous
│   └── DriftDetector.cs                          #   orphan / conformance / invisible / ambiguous findings
├── Model/                                        #   typed result (immutable records)
│   ├── InventorySnapshot.cs                      #   environments + fabrics + spokes + workloads + drift + coverage
│   ├── EnvironmentView.cs  FabricItem.cs  SpokeItem.cs  WorkloadItem.cs
│   ├── ManagedResourceGroup.cs  SubscriptionCoverage.cs
│   └── DriftFinding.cs  DriftCategory.cs
└── README.md                                     #   component overview + the read-only/credential note

src/Pdp.Inventory.Demo/                          # NEW thin console harness — the demonstrable surface (FR-022)
├── Pdp.Inventory.Demo.csproj                    #   refs the component + Azure.Identity
├── Program.cs                                    #   DefaultAzureCredential → InventoryService → human table | --json
└── README.md                                     #   "this is a demo harness, not the pdp CLI (spec 006)"

tests/Pdp.ControlPlane.Inventory.Tests/          # NEW xUnit — pure-engine unit tests (no Azure)
├── Pdp.ControlPlane.Inventory.Tests.csproj
├── ClassificationTests.cs                        #   each taxonomy branch
├── DriftDetectorTests.cs                         #   orphan / conformance / invisible / ambiguous + seed-backend exclusion
├── GroupingTests.cs                              #   workloads → environments; headline-question queries
├── TagSchemaTests.cs                             #   convention validation (region, name regex, deployed-by enum)
├── SerializationTests.cs                         #   InventorySnapshot ↔ System.Text.Json (structured-output proof, US4)
└── InventoryServiceTests.cs                      #   orchestration over a faked IResourceGraphReader (NSubstitute)

Pdp.sln                                           # EDIT — add the three projects under src/ and tests/ solution folders
```

**Structure Decision**: Mirror the spec-002 `Pdp.ControlPlane.Ipam` library shape for the reusable
component, and add the **`Pdp.Inventory.Demo`** console as the independently demonstrable surface
chosen in clarification Q3 (named to signal it is a *harness*, **not** the spec-006 `pdp` CLI). The
**`IResourceGraphReader` seam** is the key decision: it keeps the classification / grouping / drift
logic a **pure, fully unit-testable engine** (fabricated rows via NSubstitute, asserted with
Shouldly), while isolating the version-sensitive `Azure.ResourceManager.ResourceGraph` surface in a
thin adapter that the live quickstart exercises. There is **no `infra/` stack, no Azure resource, and
no GitHub workflow** because the feature is read-only and creates nothing — the execution plane is
not involved.

## Complexity Tracking

> No constitution violations. The table is intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| _(none)_ | — | — |
