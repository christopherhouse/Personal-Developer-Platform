# Tasks: Environment Inventory

**Input**: Design documents from `/specs/005-environment-inventory/`

**Prerequisites**: plan.md, spec.md, research.md, data-model.md, contracts/inventory-interfaces.md, quickstart.md

**Tests**: **xUnit unit tests ARE included** — the spec/plan call for them (research §7, plan Testing).
The classification / grouping / drift logic is a **pure engine** over an injected `IResourceGraphReader`,
so it is exhaustively unit-tested with **no Azure and no Testcontainers/Postgres**. The live success
criteria (SC-001…SC-009) are proven by the **12 quickstart.md scenarios** running the demo harness
against the deployed `westus3` fabric + spec-004 spokes.

**Organization**: Tasks grouped by user story — US1 live classified inventory (MVP), US2 scoped
headline answers, US3 tag-side drift, US4 typed structured output — each an independently
demonstrable increment.

## Key facts carried from design (do not re-derive)

| Fact | Value |
|---|---|
| Source of truth | **Azure Resource Graph only** (Article III). `ResourceContainers | where type =~ 'microsoft.resources/subscriptions/resourcegroups'`, RG-granularity (clarify Q4). Never local records / ledger / OpenTofu state. |
| SDK surface | `Azure.ResourceManager` v1.14.x (`ArmClient.GetSubscriptions()`), `Azure.ResourceManager.ResourceGraph` v1.1.0 (`TenantResource.GetResources(ResourceQueryContent)`, page via `SkipToken`, 1000/page, `allowPartialScopes`). Research §2. |
| Credential | **Pluggable `TokenCredential`** (FR-019). Demo injects `DefaultAzureCredential`; spec-006 injects managed identity. **No identity/RBAC provisioned** (SC-009). |
| Inclusion filter | `tags['pdp-managed'] =~ 'true'` (FR-003). |
| Classification | scopeCount of {`pdp-fabric`,`pdp-spoke`,`pdp-workload`}: ==1 → fabric/spoke/workload; ==0 → orphan; >1 → ambiguous (data-model §3). |
| Region | from RG **`location`** (spokes carry no region tag — spec 004); `pdp-fabric` ≠ location → conformance. |
| Environments | workloads grouped by `pdp-env`; workload missing `pdp-env` → conformance (still listed, ungrouped). |
| Drift | **informational only** (clarify Q5 / FR-016) — findings in result, inventory always succeeds. Categories: orphan / conformance / invisible / ambiguous (data-model §4). |
| Invisible drift | second query: `rg-pdp-*` named, `pdp-managed` absent/≠true (FR-014). |
| Seed backend | `RG-TF` / `cmhtfstatesa` never classified, never flagged (FR-018). |
| Deliverable | reusable `src/Pdp.ControlPlane.Inventory` (mirrors `Pdp.ControlPlane.Ipam`) + thin `src/Pdp.Inventory.Demo` console (NOT the spec-006 CLI) + `tests/Pdp.ControlPlane.Inventory.Tests`. |
| Stack | .NET 10; xUnit + Shouldly + NSubstitute; `System.Text.Json`. No infra/, no Azure resources, no GitHub workflow (read-only). |

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelizable (different files, no incomplete-task dependency)
- **[Story]**: US1–US4 per spec.md

---

## Phase 1: Setup (project scaffolding)

**Purpose**: The three .NET projects and solution wiring everything else lands on (build-green).

- [X] T001 Create class library `src/Pdp.ControlPlane.Inventory/Pdp.ControlPlane.Inventory.csproj` (SDK-style; TFM/analyzers inherit from `Directory.Build.props`) with `PackageReference`s `Azure.ResourceManager` and `Azure.ResourceManager.ResourceGraph` (pinned versions per research §2)
- [X] T002 [P] Create console harness `src/Pdp.Inventory.Demo/Pdp.Inventory.Demo.csproj` (`OutputType=Exe`) referencing the component project and `PackageReference` `Azure.Identity`
- [X] T003 [P] Create test project `tests/Pdp.ControlPlane.Inventory.Tests/Pdp.ControlPlane.Inventory.Tests.csproj` (xUnit + `Shouldly` + `NSubstitute` + `Microsoft.NET.Test.Sdk`) referencing the component project
- [X] T004 Add all three projects to `Pdp.sln` under the existing `src`/`tests` solution folders and confirm `dotnet build Pdp.sln` is clean (warnings-as-errors)

**Checkpoint**: Solution builds with three empty projects wired in.

---

## Phase 2: Foundational (blocking prerequisites)

**Purpose**: The shared domain model, tag rules, interfaces, and terminology every user story depends on.

**⚠️ CRITICAL**: No user-story work begins until this phase is complete.

- [X] T005 [P] Define the ARG boundary record `ResourceGroupRow` in `src/Pdp.ControlPlane.Inventory/Model/ResourceGroupRow.cs` (Id, Name, SubscriptionId, Location, case-insensitive Tags) per data-model §1
- [X] T006 [P] Define the output model records in `src/Pdp.ControlPlane.Inventory/Model/` — `InventorySnapshot`, `EnvironmentView`, `FabricItem`, `SpokeItem`, `WorkloadItem`, `ManagedResourceGroup`, `SubscriptionCoverage`, `DriftFinding`, and the `DriftCategory` enum — as immutable records per data-model §5
- [X] T007 [P] Implement `TagSchema` in `src/Pdp.ControlPlane.Inventory/Classification/TagSchema.cs`: the `pdp-*` tag names, allowed-value rules (region set, `[a-z0-9-]{1,24}` / `{1,16}` regexes, `pdp-deployed-by` enum), and the `RG-TF`/`cmhtfstatesa` seed-backend identifiers, per data-model §2 and `docs/conventions.md` §2
- [X] T008 [P] Add a one-line **"Drift"** entry to `docs/glossary.md` (tag-side drift: orphan / conformance / invisible / ambiguous) **before the term is used in code** (constitution Article X; research §5)
- [X] T009 Define `IResourceGraphReader` in `src/Pdp.ControlPlane.Inventory/IResourceGraphReader.cs` (DiscoverSubscriptions, QueryManagedResourceGroups, QueryLooksManagedUntagged) per contracts §2 (depends on T005/T006)
- [X] T010 Define `IInventoryService` in `src/Pdp.ControlPlane.Inventory/IInventoryService.cs` (GetSnapshot, GetEnvironments, GetSpokes, GetEnvironment) per contracts §1 (depends on T006)

**Checkpoint**: Model + interfaces compile; glossary term established; stories can begin.

---

## Phase 3: User Story 1 — Live, classified inventory across all accessible subscriptions (Priority: P1) 🎯 MVP

**Goal**: Discover every accessible subscription, query ARG for `pdp-managed` RGs, classify into
fabric/spoke/workload (workloads grouped by environment), each with subscription + region, computed
live — and render it from the demo harness.

**Independent Test**: Run `dotnet run --project src/Pdp.Inventory.Demo` against the live platform;
every `pdp-managed` RG appears, correctly classified with subscription + region; inaccessible subs
are reported, not dropped (quickstart scenarios 1, 2, 3, 5, 10).

### Tests for User Story 1

- [X] T011 [P] [US1] `ClassificationTests` in `tests/Pdp.ControlPlane.Inventory.Tests/ClassificationTests.cs` — fabric/spoke/workload happy paths, region taken from `Location`, scopeCount==0/>1 returns unclassified (asserted with Shouldly)
- [X] T012 [P] [US1] `InventoryServiceTests` in `tests/Pdp.ControlPlane.Inventory.Tests/InventoryServiceTests.cs` — orchestration over a **NSubstitute-faked `IResourceGraphReader`**: snapshot assembles taxonomy + environment grouping + coverage from fabricated rows (no Azure)

### Implementation for User Story 1

- [X] T013 [US1] Implement `ResourceGroupClassifier` in `src/Pdp.ControlPlane.Inventory/Classification/ResourceGroupClassifier.cs` — managed row → fabric/spoke/workload item or unclassified reason (orphan/ambiguous), region from `Location`, per data-model §3 (depends on T006/T007)
- [X] T014 [US1] Implement the managed-RG KQL in `src/Pdp.ControlPlane.Inventory/Queries/InventoryQueries.cs` (the `ResourceContainers` projection, research §1)
- [X] T015 [US1] Implement `ResourceGraphReader` in `src/Pdp.ControlPlane.Inventory/ResourceGraphReader.cs` — `ArmClient.GetSubscriptions()` → `SubscriptionCoverage` (Queried/Inaccessible), and `QueryManagedResourceGroupsAsync` with `SkipToken` paging over the subscription scope; takes an injected `TokenCredential`/`ArmClient` (FR-019), `allowPartialScopes` for coverage honesty (depends on T009/T014)
- [X] T016 [US1] Implement `InventoryService.GetSnapshotAsync` in `src/Pdp.ControlPlane.Inventory/InventoryService.cs` — discover → query → classify → group workloads by `pdp-env` → assemble `InventorySnapshot` with `Coverage` (depends on T013/T015)
- [X] T017 [US1] Implement `src/Pdp.Inventory.Demo/Program.cs` — build `DefaultAzureCredential` → `ArmClient` → `InventoryService`, call `GetSnapshotAsync`, render the human-readable taxonomy + coverage tables (depends on T016)

**Checkpoint**: MVP — live classified inventory demonstrable (SC-001, SC-002, SC-008). **STOP & VALIDATE**.

---

## Phase 4: User Story 2 — Scoped answers to the headline questions (Priority: P2)

**Goal**: Answer "which spokes, in which subscription/region?" and "what is in environment X?" as
filtered projections over the snapshot.

**Independent Test**: Query spokes-by-subscription/region and contents of a named environment;
unknown names return empty cleanly, not errors (quickstart scenarios 3, 4).

### Tests for User Story 2

- [X] T018 [P] [US2] `GroupingTests` in `tests/Pdp.ControlPlane.Inventory.Tests/GroupingTests.cs` — environments list, spokes list with sub+region, environment-X projection returns exactly its workloads, unknown env → empty (not error)

### Implementation for User Story 2

- [X] T019 [US2] Implement `GetEnvironmentsAsync`, `GetSpokesAsync`, `GetEnvironmentAsync(name)` projections in `src/Pdp.ControlPlane.Inventory/InventoryService.cs` per contracts §1 / data-model §5 (depends on T016)
- [X] T020 [US2] Add scoped-view rendering to `src/Pdp.Inventory.Demo/Program.cs` — a spokes-by-subscription/region section and an environments section (depends on T019)

**Checkpoint**: Headline questions answerable (SC-004).

---

## Phase 5: User Story 3 — Surface tag-conformance drift (Priority: P2)

**Goal**: Emit orphan / conformance / invisible / ambiguous findings as informational result data;
never flag the seed backend; inventory always succeeds.

**Independent Test**: Deliberately mis-tag / orphan / dual-tag an RG and create an `rg-pdp-*` group
without `pdp-managed`; each is flagged with the right category; correctly tagged RGs and `RG-TF` are
not (quickstart scenarios 6, 7, 8).

### Tests for User Story 3

- [X] T021 [P] [US3] `DriftDetectorTests` in `tests/Pdp.ControlPlane.Inventory.Tests/DriftDetectorTests.cs` — orphan, ambiguous, conformance (bad region / bad name / bad `pdp-deployed-by` / workload missing `pdp-env` / `pdp-fabric`≠location), invisible, and **seed-backend exclusion**
- [X] T022 [P] [US3] `TagSchemaTests` in `tests/Pdp.ControlPlane.Inventory.Tests/TagSchemaTests.cs` — value-rule validation (region set, name regexes, deployed-by enum)

### Implementation for User Story 3

- [X] T023 [US3] Implement `DriftDetector` in `src/Pdp.ControlPlane.Inventory/Classification/DriftDetector.cs` — orphan/ambiguous from classification, conformance from `TagSchema`, seed-backend exclusion, per data-model §4 (depends on T007/T013)
- [X] T024 [US3] Add the looks-managed-but-untagged KQL to `InventoryQueries` and implement `QueryLooksManagedUntaggedAsync` in `ResourceGraphReader` (invisible drift, FR-014; research §1) (depends on T015)
- [X] T025 [US3] Wire drift into `InventoryService.GetSnapshotAsync` — populate `Snapshot.Drift` from `DriftDetector` + invisible-query rows; guarantee the call **always succeeds** regardless of findings (FR-016) (depends on T023/T024)
- [X] T026 [US3] Add a drift-summary section to `src/Pdp.Inventory.Demo/Program.cs` (category + RG + offending tag) (depends on T025)

**Checkpoint**: Drift surfaced informationally (SC-003).

---

## Phase 6: User Story 4 — Typed, structured results for programmatic consumers (Priority: P3)

**Goal**: The snapshot is consumable as typed structured data (and `--json`) by future 006/007
layers, not display-only.

**Independent Test**: `dotnet run --project src/Pdp.Inventory.Demo -- --json` emits the taxonomy +
per-item sub/region + environment grouping + drift as structured JSON; assertable without scraping
text (quickstart scenario 9).

### Tests for User Story 4

- [X] T027 [P] [US4] `SerializationTests` in `tests/Pdp.ControlPlane.Inventory.Tests/SerializationTests.cs` — `InventorySnapshot` serializes via `System.Text.Json` exposing environments/fabrics/spokes/workloads (sub+region)/drift/coverage; `DriftCategory` as string

### Implementation for User Story 4

- [X] T028 [US4] Ensure the `Model/` records are serialization-friendly (string-enum converter for `DriftCategory`, stable property names) in `src/Pdp.ControlPlane.Inventory/Model/` (depends on T006) — realized centrally as `InventoryJson` (canonical `JsonSerializerOptions`)
- [X] T029 [US4] Implement the `--json` branch in `src/Pdp.Inventory.Demo/Program.cs` — serialize the same `InventorySnapshot` with `System.Text.Json` (no re-query) per contracts §4 (depends on T017/T028)

**Checkpoint**: Structured output proven (SC-006).

---

## Phase 7: Polish & Cross-Cutting Concerns

- [X] T030 [P] Write `src/Pdp.ControlPlane.Inventory/README.md` — component overview, the ARG-only/Article-III stance, and the read-only/pluggable-credential note
- [X] T031 [P] Write `src/Pdp.Inventory.Demo/README.md` — "demonstrable harness, **not** the spec-006 `pdp` CLI", usage + `--json`
- [X] T032 Verify the latency budget against the live platform — time the full sweep (P95 < 5 s) and a scoped query (< 2 s) (SC-005) — **PASS**: full P95 ~3.1s, scoped P95 ~0.77s (projections take a taxonomy-only path: one ARG query, no invisible/drift work)
- [X] T033 Run all 12 `quickstart.md` scenarios end-to-end: confirm **zero** write/delete ops in the Azure activity log over the run window (SC-007), confirm the run reads **only** Azure Resource Graph and never the IPAM ledger or the (future) registry (FR-002/FR-020), confirm nothing was provisioned to tear down (SC-009), and revert any deliberate mis-tags — read-only scenarios validated live (1,2,3,7-orphan,9,11,12); mutation-requiring fixtures (5,6,8,10, ambiguous) proven by unit tests since live mutation is out of this read-only spec's scope; SC-007 confirmed (zero write/delete on pdp/seed RGs); no mis-tags created (revert = no-op)
- [X] T034 [P] Run `dotnet format` + analyzers clean across the three new projects

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (P1)**: no dependencies.
- **Foundational (P2)**: depends on Setup — **blocks all stories**.
- **US1 (P3)**: depends on Foundational. **MVP.**
- **US2 (P4)**: depends on US1's `GetSnapshotAsync` (T016) — projections over the snapshot.
- **US3 (P5)**: depends on US1 (classifier T013, reader T015, service T016) — adds drift.
- **US4 (P6)**: depends on US1 (T017) + the model (T006) — adds structured/`--json` output.
- **Polish (P7)**: after the desired stories.

> Note: US2/US3/US4 each extend `InventoryService`/`Program.cs` from US1, so they are **sequenced
> after US1** rather than fully parallel. They are independently *testable* and *demonstrable*, but
> share files — coordinate edits to `InventoryService.cs` and `Program.cs`.

### Within each story

- Unit tests (faked reader) alongside implementation; the pure engine (classifier, drift, grouping)
  is test-first where practical.
- Model → reader/queries → service → harness rendering.

### Parallel opportunities

- Setup: T002, T003 in parallel after T001.
- Foundational: T005, T006, T007, T008 in parallel; then T009/T010.
- Per story, the test files marked [P] are parallel (distinct files); implementation tasks touching
  `InventoryService.cs` / `Program.cs` are **not** parallel with each other.
- Polish: T030, T031, T034 in parallel.

---

## Parallel Example: Foundational

```text
# After T001–T004, launch the model/rules/glossary in parallel:
Task: "Define ResourceGroupRow in Model/ResourceGroupRow.cs"            # T005
Task: "Define output model records in Model/"                           # T006
Task: "Implement TagSchema in Classification/TagSchema.cs"             # T007
Task: "Add Drift entry to docs/glossary.md"                            # T008
```

---

## Implementation Strategy

### MVP first (US1 only)

1. Phase 1 Setup → Phase 2 Foundational → Phase 3 US1.
2. **STOP & VALIDATE**: run the harness live against `westus3` — every `pdp-managed` RG classified
   with sub + region, coverage honest (SC-001/002/008).
3. Demo.

### Incremental delivery

US1 (MVP) → US2 (headline queries) → US3 (drift) → US4 (structured/`--json`) → Polish. Each story is
an independently demonstrable increment that does not break the previous one.

---

## Notes

- [P] = different files, no incomplete-task dependency.
- No Azure in unit tests (NSubstitute fakes `IResourceGraphReader`); no Testcontainers/Postgres.
- Read-only throughout: no Azure mutation, no provisioned identity/RBAC, nothing to tear down.
- Commit after each task or logical group; the repo commits on PR/merge (auto-commit hooks are
  disabled in `git-config.yml`).
