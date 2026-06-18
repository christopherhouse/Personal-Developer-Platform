---
description: "Task list for Action Layer — Control Plane (spec 006)"
---

# Tasks: Action Layer — Control Plane

**Input**: Design documents from `/specs/006-control-plane/`

**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅

**Tests**: INCLUDED — the spec, plan §Testing, and quickstart require integration tests, and the
constitution mandates real-Postgres testing for the ledger/registry/saga/outbox (untestable
in-memory). GitHub and the webhook are faked at their seams (WireMock.Net / WebApplicationFactory).

**Organization**: by user story (US1–US5 from spec.md), in priority order. MVP = **US1 + US2** (both
P1 — US1 is the vend spine; US2 adds the Article VIII plan/confirm gate + teardown release).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelizable (different files, no incomplete-task dependency)
- **[Story]**: US1–US5 for story-phase tasks; Setup/Foundational/Polish carry no story label
- Exact file paths included

## Path conventions

New .NET projects under `src/` and `tests/` wired into `Pdp.sln`; workflow files under
`.github/workflows/`. Mirrors the existing `Pdp.ControlPlane.*` layout (specs 002/005).

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: scaffold the projects and dependencies the whole control plane builds on.

- [X] T001 [P] Create class library `src/Pdp.ControlPlane.Registry/` (net10.0) and add to `Pdp.sln`
- [X] T002 [P] Create class library `src/Pdp.ControlPlane.Dispatch/` (net10.0) and add to `Pdp.sln`
- [X] T003 [P] Create class library `src/Pdp.ControlPlane.Verbs/` (net10.0) and add to `Pdp.sln`
- [X] T004 [P] Create ASP.NET Core minimal-API project `src/Pdp.ControlPlane.Api/` and add to `Pdp.sln`
- [X] T005 [P] Create YARP project `src/Pdp.ControlPlane.Ingress/` and add to `Pdp.sln`
- [X] T006 [P] Create console project `src/Pdp.Cli/` (assembly name `pdp`) and add to `Pdp.sln`
- [X] T007 [P] Create .NET Aspire AppHost `src/Pdp.AppHost/` and add to `Pdp.sln`
- [X] T008 [P] Create test projects `tests/Pdp.ControlPlane.Registry.Tests/`, `tests/Pdp.ControlPlane.Verbs.Tests/`, `tests/Pdp.ControlPlane.Dispatch.Tests/`, `tests/Pdp.ControlPlane.Api.Tests/`, `tests/Pdp.Cli.Tests/`; add to `Pdp.sln`
- [X] T009 Add NuGet references per plan (Wolverine, WolverineFx.Postgresql, Microsoft.EntityFrameworkCore + Npgsql.EntityFrameworkCore.PostgreSQL + EFCore.NamingConventions, Octokit, GitHubJwt, Octokit.Webhooks.AspNetCore, Yarp.ReverseProxy, FluentValidation, Microsoft.Extensions.Http.Resilience, System.CommandLine, Azure.Identity, Azure.ResourceManager(.ResourceGraph), Azure.Monitor.OpenTelemetry.AspNetCore; tests: xUnit, Shouldly, NSubstitute, Testcontainers.PostgreSql, Respawn, WireMock.Net, Microsoft.AspNetCore.Mvc.Testing) — pinned via the repo's central package management
- [X] T010 Wire project references (Verbs → Registry/Dispatch/Pdp.ControlPlane.Ipam/Pdp.ControlPlane.Inventory; Api → Verbs/Dispatch/Registry; Ingress → none [proxy only]; Cli → Verbs; AppHost → Api/Ingress; each *.Tests → its target) — confirm `dotnet build` succeeds
- [X] T011 [P] Add `README.md` stub to each new project noting "control plane (spec 006); production ACA hosting + ingress + App Insights resource = spec 007"

**Checkpoint**: solution builds with empty projects wired together.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: persistence, the dispatch/track seams, Wolverine host, DI, and telemetry that ALL stories
depend on. **No user story may start until this phase is complete.**

- [X] T012 [P] Implement enums (`EnvironmentKind`, `EnvironmentStatus`, `RunPhase`, `RunOutcome`, `TrackingSource`) in `src/Pdp.ControlPlane.Registry/Entities/`
- [X] T013 [P] Implement `Environment` + `ProvisioningRun` entities (data-model §1–2) in `src/Pdp.ControlPlane.Registry/Entities/`
- [X] T014 Implement `EnvironmentSaga` state class (data-model §3) in `src/Pdp.ControlPlane.Registry/EnvironmentSaga.cs`
- [X] T015 Implement `RegistryDbContext` (`registry` schema, snake_case via EFCore.NamingConventions, `IPNetwork` mapping, unique natural-key index on `(kind, subscription, name)`) in `src/Pdp.ControlPlane.Registry/RegistryDbContext.cs`
- [X] T016 Create EF Core migration `InitialRegistrySchema` (environments, provisioning_runs, saga storage) in `src/Pdp.ControlPlane.Registry/Migrations/`
- [X] T017 [P] Implement verb request/result model (`SpokeCreateRequest` [no Cidr], `FabricCreateRequest`, `DestroyRequest`, `EnvRef`, `Confirmation`, `PlanResult`, `VerbResult`) in `src/Pdp.ControlPlane.Verbs/Model/`
- [X] T018 [P] Implement `ControlPlaneOptions`/`GitHubAppOptions` config binding (App id/installation id/private key, repo, default branch, Postgres conn, App Insights conn) in `src/Pdp.ControlPlane.Dispatch/` + `src/Pdp.ControlPlane.Verbs/`
- [X] T019 Implement `GitHubAppCredential` (GitHubJwt → `CreateInstallationToken`, cached installation token) in `src/Pdp.ControlPlane.Dispatch/GitHubAppCredential.cs`
- [X] T020 Define dispatch/track seams (`IWorkflowDispatcher`, `WorkflowDispatch`, `IRunTracker`, `WorkflowRunStatus`) in `src/Pdp.ControlPlane.Dispatch/`
- [X] T021 Implement Wolverine host configuration module (`PersistMessagesWithPostgresql` + `UseEntityFrameworkCoreTransactions` + `Policies.UseDurableLocalQueues` + EF Core saga persistence on `RegistryDbContext`) in `src/Pdp.ControlPlane.Verbs/WolverineConfiguration.cs`
- [X] T022 [P] Implement OpenTelemetry/`UseAzureMonitor()` wiring + `PdpActivitySource`/`Meter` with an `env_id`-tagging helper in `src/Pdp.ControlPlane.Verbs/Telemetry/`
- [X] T023 Implement `AddControlPlaneVerbs(...)` DI extension (wires Registry, Dispatch, Ipam, Inventory, Wolverine, FluentValidation) in `src/Pdp.ControlPlane.Verbs/ServiceCollectionExtensions.cs`
- [X] T024 Implement a **shared** Testcontainers-Postgres + Respawn fixture (applies IPAM + registry migrations) in a `Pdp.ControlPlane.TestSupport` helper referenced by Verbs.Tests, Registry.Tests, Dispatch.Tests, and Api.Tests; add shared WireMock.Net (fake GitHub) and `WebApplicationFactory` fixtures alongside it (resolves analysis F1)
- [X] T024a [P] Registry.Tests: the `InitialRegistrySchema` migration applies the `registry` schema and `dotnet ef database update 0` drops it cleanly (Testcontainers) — schema half of SC-010, in `tests/Pdp.ControlPlane.Registry.Tests/SchemaMigrationTests.cs`

**Checkpoint**: persistence + seams + host wiring ready — user stories can begin.

---

## Phase 3: User Story 1 — One-command spoke vend, validated → allocated → dispatched → tracked (P1) 🎯 MVP

**Goal**: `pdp spoke create` allocates the block live from the ledger (Gate-G1), records intent,
dispatches the apply workflow, and tracks it to a recorded outcome; the spoke appears in inventory.

**Independent Test**: run `pdp spoke create` against `westus3` + a writable sub; confirm a ledger row
was created by size, the workflow was dispatched with `env_id`/`spoke_cidr`, the run was tracked to
success, and the spoke is discoverable in inventory (quickstart Scenario 1).

### Tests for User Story 1 ⚠️ (write first; ensure they fail)

- [X] T025 [P] [US1] Integration test (Testcontainers): `spoke create` allocates a ledger block by `size`, writes the `environments` row (UUIDv7, natural key) + a `provisioning_runs` row, atomically (rollback leaves no leak) in `tests/Pdp.ControlPlane.Verbs.Tests/SpokeCreateTests.cs`
- [X] T026 [P] [US1] Integration test (WireMock.Net): dispatcher sends exact inputs (`env_id`, `mode=apply`, `spoke_cidr`, subscription/region/name) and sets the `env_id` `run-name` in `tests/Pdp.ControlPlane.Dispatch.Tests/DispatchInputsTests.cs`
- [X] T027 [P] [US1] Integration test (WireMock.Net): reconciler correlates a run by `run-name` → `env_id` and drives the environment to `Active` in `tests/Pdp.ControlPlane.Dispatch.Tests/ReconcileTests.cs`
- [X] T028 [P] [US1] Integration test: `spoke create` fails fast on unregistered region / no fabric / exhausted space — no leaked allocation, no orphan row in `tests/Pdp.ControlPlane.Verbs.Tests/SpokeCreateFailFastTests.cs`
- [X] T028a [P] [US1] Registry.Tests (Testcontainers): **idempotent natural-key convergence** (re-create of `(kind, subscription, name)` returns the same `env_id` — FR-022), **single-flight guard** (a non-terminal env rejects a new mutation with `OperationInProgressException` — FR-022a), and **status transitions** (Requested→Provisioning→Active/Failed; Destroying→Destroyed) in `tests/Pdp.ControlPlane.Registry.Tests/EnvironmentRegistryTests.cs` (resolves analysis C1)

### Implementation for User Story 1

- [X] T029 [US1] Implement `EnvironmentRegistry` (create/get env with UUIDv7 + natural-key upsert, status transitions, **single-flight guard** FR-022a) in `src/Pdp.ControlPlane.Registry/EnvironmentRegistry.cs`
- [X] T030 [US1] Implement `GitHubWorkflowDispatcher` (Octokit `Actions.Workflows.CreateDispatch` + Polly resilience + `env_id` `run-name`; durable-outbox enqueue) in `src/Pdp.ControlPlane.Dispatch/GitHubWorkflowDispatcher.cs`
- [X] T031 [US1] Implement `RunTracker.RecordRunStatusAsync` (parse `run-name` → `env_id`, idempotent first-terminal-wins record, emit `RunCompleted`/`RunFailed`) in `src/Pdp.ControlPlane.Dispatch/RunTracker.cs`
- [X] T032 [US1] Implement `RunReconciler` (Wolverine self-rescheduling scheduled message ≤60 s; list in-flight; query GitHub run status) in `src/Pdp.ControlPlane.Dispatch/RunReconciler.cs`
- [X] T033 [US1] Implement `EnvironmentSaga` create handlers: `Start(SpokeCreateRequested)` → `IIpamLedger.AllocateAsync(region,name,size)` + record intent + dispatch(`mode=apply`) in **one outbox transaction**; `Handle(RunCompleted/Failed)` → `Active`/`Failed` (release provisional allocation on failed create — FR-011) in `src/Pdp.ControlPlane.Verbs/Lifecycle/EnvironmentSagaHandlers.cs`
- [X] T034 [US1] Implement `ISpokeVerbs.CreateAsync` orchestration (validate → start saga → return `VerbResult`; **no Cidr input** — Gate-G1) in `src/Pdp.ControlPlane.Verbs/Handlers/SpokeVerbs.cs`
- [X] T035 [P] [US1] Implement `SpokeCreateRequest` FluentValidation validator in `src/Pdp.ControlPlane.Verbs/Validation/SpokeCreateRequestValidator.cs`
- [X] T036 [US1] Edit `.github/workflows/spoke-vend.yml`: add `env_id` + `mode` inputs, set `run-name: pdp ${{ inputs.mode }} ${{ inputs.env_id }}`, gate on `mode`; on `mode=plan` run `tofu plan -no-color` → write Step Summary + **upload `plan.txt`/`plan.json` artifact** (no apply); `mode=apply` performs the apply (U1 plan-output path)
- [X] T037 [US1] Implement `pdp spoke create` command (System.CommandLine `--subscription/--region/--name/--size/--yes`, no `--cidr`) in `src/Pdp.Cli/Commands/SpokeCommand.cs`
- [X] T038 [US1] Implement CLI in-process host wiring (`AddControlPlaneVerbs`, owner `DefaultAzureCredential`, Postgres/GitHub config); a mutating command **dispatches then polls the registry/GitHub correlation to terminal**, or returns immediately with **`--no-wait`** (prints `env_id`/run URL, leaving completion to the Api host's reconciler) — the durable reconciler lives in the Api host, not the CLI (contracts/cli-surface.md §4; resolves analysis A1) in `src/Pdp.Cli/Program.cs`
- [X] T039 [P] [US1] Implement human + `--json` renderers for `VerbResult` in `src/Pdp.Cli/Rendering/`
- [X] T040 [P] [US1] CLI test: `spoke create` parsing, `--json` output, exit codes (NSubstitute over `ISpokeVerbs`) in `tests/Pdp.Cli.Tests/SpokeCommandTests.cs`

**Checkpoint**: a spoke can be vended end to end by one command, tracked to success, visible in inventory.

> ⚠️ **Constitutional note (analysis I1)**: US1 alone dispatches `mode=apply` **without** the Article VIII
> plan/confirm gate — it is an intermediate development increment, **not** shippable on its own. The
> feature is constitutionally complete only after **US2** adds plan-before-apply + confirm-before-destroy.
> Do not use US1 against live infrastructure before US2 lands.

---

## Phase 4: User Story 2 — Plan before apply, confirm before destroy + teardown release (P1)

**Goal**: every mutation surfaces a plan before apply; every destroy needs explicit confirmation;
`spoke destroy` releases the IPAM allocation on success (completing spec 004's deferred teardown).

**Independent Test**: confirm create shows a plan before applying; `spoke destroy` refuses without
`--confirm`, and after a successful destroy the allocation is released and reusable (quickstart
Scenarios 2–3).

### Tests for User Story 2 ⚠️

- [X] T041 [P] [US2] Integration test: a `mode=plan` run is dispatched and its `PlanSummary` surfaced **before** any apply (two-phase) in `tests/Pdp.ControlPlane.Verbs.Tests/PlanConfirmTests.cs`
- [X] T042 [P] [US2] Integration test: `spoke destroy` requires confirmation; on success calls `ReleaseAsync` (block reusable); on failed/partial destroy does **not** release in `tests/Pdp.ControlPlane.Verbs.Tests/SpokeDestroyTests.cs`

### Implementation for User Story 2

- [X] T043 [US2] Implement two-phase orchestration (`PlanCreateAsync`/`PlanDestroyAsync` dispatch `mode=plan`; `ConfirmationGiven` gates the apply/destroy dispatch) in `src/Pdp.ControlPlane.Verbs/PlanConfirm/` + saga `PendingConfirmation` handling
- [X] T043a [US2] Implement plan-output retrieval in `IRunTracker`: after a `mode=plan` run is terminal, **download the `plan.txt`/`plan.json` artifact** via the GitHub Actions API (`Actions.Artifacts.ListWorkflowArtifacts`→`DownloadArtifact`, installation-token auth) into `ProvisioningRun.PlanSummary`; a missing/oversized artifact surfaces the run URL and blocks auto-confirm (contracts/dispatch-and-tracking.md §6) in `src/Pdp.ControlPlane.Dispatch/RunTracker.cs` (resolves analysis U1)
- [X] T044 [US2] Implement `ISpokeVerbs.PlanDestroyAsync`/`DestroyAsync` (confirm → dispatch destroy → on success `IIpamLedger.ReleaseAsync` + mark `Destroyed`; FR-009/FR-011) in `src/Pdp.ControlPlane.Verbs/Handlers/SpokeVerbs.cs`
- [X] T045 [US2] Implement `Confirmation` enforcement (restate target; unbypassable) + `OperationInProgressException` (single-flight reject) surfacing in `src/Pdp.ControlPlane.Verbs/`
- [X] T046 [US2] Edit `.github/workflows/spoke-destroy.yml`: add `env_id` + `mode` inputs + `env_id` `run-name`; `mode=plan` runs `tofu plan -destroy` → Step Summary + `plan.txt` artifact (destroy preview), `mode=destroy` performs the destroy
- [X] T047 [US2] Implement `pdp spoke destroy` (+ `--confirm <name>`, **no `--yes`**) and add plan-display/`--yes` prompt to `spoke create` in `src/Pdp.Cli/Commands/SpokeCommand.cs`

**Checkpoint**: US1+US2 = the trustworthy MVP — vend and destroy with plan/confirm and clean release.

---

## Phase 5: User Story 3 — Full verb surface across specs 2–5 via the CLI (P2)

**Goal**: `pdp fabric|ipam|inventory|env` all work through the same verb layer with human + `--json`;
fabric create dispatches the new `fabric-vend.yml`; inventory reuses the spec-005 component.

**Independent Test**: invoke each verb in both output modes; confirm fabric create/destroy dispatch
their workflows, IPAM verbs pass through to the ledger, and inventory answers come from ARG via the
reused component (quickstart Scenarios 4–5).

### Tests for User Story 3 ⚠️

- [X] T048 [P] [US3] Integration test (WireMock.Net): `fabric create` dispatches `fabric-vend.yml`; `fabric destroy` is confirm-gated in `tests/Pdp.ControlPlane.Verbs.Tests/FabricVerbsTests.cs`
- [X] T049 [P] [US3] Test: IPAM verbs pass through to `IIpamLedger`; inventory/env verbs delegate to `Pdp.ControlPlane.Inventory` with the injected credential (no duplicated logic) in `tests/Pdp.ControlPlane.Verbs.Tests/PassthroughVerbsTests.cs`

### Implementation for User Story 3

- [X] T050 [US3] Create `.github/workflows/fabric-vend.yml` (`workflow_dispatch` over `infra/fabric`; `env_id`/`mode` inputs + `run-name`; `mode=plan` writes Step Summary + uploads `plan.txt`/`plan.json` artifact, `mode=apply` applies; OIDC unchanged) — FR-012a + U1 plan-output path
- [X] T051 [US3] Edit `.github/workflows/fabric-destroy.yml`: add `env_id` + `mode` inputs + `run-name`; `mode=plan` runs `tofu plan -destroy` → Step Summary + `plan.txt` artifact, `mode=destroy` performs the destroy
- [X] T052 [US3] Implement `IFabricVerbs` (Plan/Create/Destroy; `RegisterRegionAsync` when needed) in `src/Pdp.ControlPlane.Verbs/Handlers/FabricVerbs.cs`
- [X] T053 [P] [US3] Implement `IIpamVerbs` pass-through (allocate/release/query/query-all) in `src/Pdp.ControlPlane.Verbs/Handlers/IpamVerbs.cs`
- [X] T054 [P] [US3] Implement `IInventoryVerbs` delegating to `Pdp.ControlPlane.Inventory` with the control plane's `TokenCredential` injected (FR-013) in `src/Pdp.ControlPlane.Verbs/Handlers/InventoryVerbs.cs`
- [X] T055 [US3] Implement `pdp fabric`, `pdp ipam`, `pdp inventory`, `pdp env` commands + renderers in `src/Pdp.Cli/Commands/`
- [X] T056 [P] [US3] CLI test: all verb commands parse and render human + `--json` in `tests/Pdp.Cli.Tests/VerbCommandsTests.cs`

**Checkpoint**: the whole platform is operable from one CLI.

---

## Phase 6: User Story 4 — Intent registry + provisioning-run audit trail (P2)

**Goal**: query "what did I ask for and what happened?" — environment intent/status + the run audit
trail — while "what's deployed?" stays inventory/ARG (division of truth).

**Independent Test**: after a create + destroy, the env record shows the right status history and each
dispatched run is captured with inputs/run id/url/outcome; deployed-state still comes from inventory
(quickstart Scenario 6).

### Tests for User Story 4 ⚠️

- [X] T057 [P] [US4] Integration test: status transitions recorded; `provisioning_runs` capture `DispatchInputs` (jsonb), GitHub run id/url, `PlanSummary`, `TrackedBy`, outcome; "deployed" query routes to inventory, not the registry (FR-016) in `tests/Pdp.ControlPlane.Verbs.Tests/RegistryAuditTests.cs`

### Implementation for User Story 4

- [X] T058 [US4] Complete provisioning-run audit fields (jsonb `DispatchInputs`, `PlanSummary` capture, `TrackedBy`, `CompletedAt`) in dispatcher/tracker write paths in `src/Pdp.ControlPlane.Dispatch/` — already populated by the US1/US2 `RunTracker`/saga write paths; T057 asserts the full capture
- [X] T059 [US4] Implement `IRunVerbs` (`GetEnvironmentAsync`, `GetRunsAsync`, `GetRunAsync`) in `src/Pdp.ControlPlane.Verbs/Handlers/RunVerbs.cs`
- [X] T060 [US4] Implement `pdp run list/show` commands + renderers in `src/Pdp.Cli/Commands/RunCommand.cs`

**Checkpoint**: full audit/history queryable; division of truth verified.

---

## Phase 7: User Story 5 — Resilient completion tracking (webhook + polling reconcile) (P3)

**Goal**: low-latency completion via the `workflow_run` webhook (YARP ingress → internal handler),
with the polling reconciler (US1) guaranteeing completion even when a delivery is missed; deliveries
are idempotent.

**Independent Test**: deliver a webhook and see the terminal record; suppress it and confirm the
reconciler still drives the run terminal within the bound; deliver a duplicate and confirm the outcome
is unchanged (quickstart Scenario 7).

### Tests for User Story 5 ⚠️

- [X] T061 [P] [US5] Integration test (WebApplicationFactory): webhook endpoint validates HMAC (`X-Hub-Signature-256`), parses typed `workflow_run`, records terminal outcome via the inbox in `tests/Pdp.ControlPlane.Api.Tests/WebhookHandlerTests.cs`
- [X] T062 [P] [US5] Integration test (WireMock.Net): **missed** webhook → reconciler drives terminal within bound; **duplicate** webhook → idempotent (outcome unchanged, `TrackedBy` first-wins) in `tests/Pdp.ControlPlane.Api.Tests/TrackingResilienceTests.cs`

### Implementation for User Story 5

- [X] T063 [US5] Implement `GitHubWebhookHandler` (`Octokit.Webhooks.AspNetCore` HMAC + typed `workflow_run` → durable inbox → `RecordRunStatusAsync`) in `src/Pdp.ControlPlane.Api/Webhooks/GitHubWebhookHandler.cs`
- [X] T064 [US5] Implement idempotent dedupe on `(EnvId, GitHubRunId)` + `TrackingSource` recording in `src/Pdp.ControlPlane.Dispatch/RunTracker.cs` — the dedupe (unique `(env_id, github_run_id)` index + first-terminal-wins + unique-violation catch) was built with the US1 tracker; US5 exercises it via the webhook path and proves it in T062
- [X] T065 [US5] Implement the YARP ingress (`Pdp.ControlPlane.Ingress`) forwarding `POST /webhooks/github` → internal Api; no business logic in `src/Pdp.ControlPlane.Ingress/Program.cs` + `appsettings.json`
- [X] T066 [US5] Wire the Api host (`Program.cs`: `AddControlPlaneVerbs` + Wolverine outbox/inbox + `UseAzureMonitor` + reconciler schedule) and the Aspire AppHost (Postgres + Api + Ingress) in `src/Pdp.ControlPlane.Api/Program.cs` + `src/Pdp.AppHost/AppHost.cs`

**Checkpoint**: tracking is resilient and host-ready for spec 007.

---

## Phase 8: Polish & Cross-Cutting Concerns

- [X] T067 [P] Flesh out each project `README.md` (purpose, the host-deferred/Article-IX-ingress note)
- [X] T068 [P] Verify no prohibited deps (no MediatR/MassTransit/AutoMapper/Moq/Serilog/FluentAssertions v8+); run `dotnet format`; analyzers/warnings-as-errors clean
- [X] T069 Run full `dotnet test` (unit + Testcontainers integration + WireMock + WAF) — all green
- [X] T070 Verify SC-011 (no public endpoint except the webhook ingress; no standing cloud write credential) and SC-013 (`env_id`-correlated traces in dev App Insights / console exporter) — SC-011 verified by inspection (only `Pdp.ControlPlane.Ingress` maps a public proxy → internal HMAC-validated handler; cloud auth is owner-context `DefaultAzureCredential`, no stored cloud secret; GitHub App key + webhook secret are the only non-Azure secrets); SC-013 covered by `TelemetryTests` (`env_id`-tagged spans on `Pdp.ControlPlane` source)
- [X] T070a [P] Verify SC-012: integration test (WireMock.Net fake GitHub) asserts the control-plane **dispatch-ack path returns within the budget** (P95 < 5 s, isolated from real IaC run duration), and fail-fast errors return in the same budget without dispatching, in `tests/Pdp.ControlPlane.Dispatch.Tests/DispatchLatencyTests.cs` (resolves analysis G1)
- [ ] T071 Run quickstart.md Scenarios 1–8 live against the `westus3` fabric + a writable target sub — **owner-run live acceptance** (real Azure mutations + GitHub `workflow_dispatch` under the owner's `az login` context; not executable from CI/agent). **DEFERRED (2026-06-18):** the live ledger Postgres is private VNet-injected + Entra-only and unreachable from the owner's laptop (no VNet access; a `/32` firewall rule is impossible on a VNet-injected server). Unblocked by spec 7 (in-VNet ACA host). The `pdp-orchestrator` GitHub App is already created/installed and wired.
- [ ] T072 Run quickstart teardown (destroy created spokes/fabric — allocations released; `dotnet ef database update 0` drops the `registry` schema) and confirm zero residual footprint (SC-010) — **owner-run live acceptance** (confirmed destroy, Article VIII). **DEFERRED (2026-06-18):** same blocker as T071 — runs with the live acceptance once in-VNet access exists.

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (P1)** → no deps.
- **Foundational (P2)** → after Setup; **blocks all user stories**.
- **US1 (P3)** → after Foundational. The MVP spine.
- **US2 (P4)** → after US1 (extends spoke create/destroy + saga with plan/confirm + release).
- **US3 (P5)** → after Foundational; independent of US2 (reuses US1's dispatch/track; fabric/ipam/inventory verbs are new files).
- **US4 (P6)** → after US1 (reads the env/run records US1 writes).
- **US5 (P7)** → after US1 (webhook feeds the same `RunTracker`/saga; reconciler is from US1).
- **Polish (P8)** → after all desired stories.

### Within each story

- Tests first (must fail), then models → registry/dispatch services → saga/verb orchestration →
  workflow edits → CLI. Same-file tasks are sequential; `[P]` tasks touch different files.

### Parallel opportunities

- Setup T001–T008, T011 all `[P]` (distinct projects).
- Foundational T012, T013, T017, T018, T022, T024a `[P]`; T014–T016 sequential (same DbContext/migration).
- US1 tests T025–T028, T028a `[P]`; T035, T039, T040 `[P]`.
- US3 T053, T054, T056 `[P]`; US5 T061, T062 `[P]`; Polish T070a `[P]`.
- US2 T043a (plan-output download) depends on the plan-run path (T036/T043) being terminal first.
- After Foundational, **US3 can run in parallel with the US1→US2 chain** by a second developer.

---

## Parallel Example: User Story 1

```bash
# Tests first (all parallel — different files):
Task: T025 SpokeCreateTests (Testcontainers)
Task: T026 DispatchInputsTests (WireMock.Net)
Task: T027 ReconcileTests (WireMock.Net)
Task: T028 SpokeCreateFailFastTests

# Then parallel implementation slices:
Task: T035 SpokeCreateRequestValidator
Task: T039 VerbResult renderers
```

---

## Implementation Strategy

### MVP first (US1 + US2 — both P1)

1. Phase 1 Setup → Phase 2 Foundational (critical; blocks everything).
2. Phase 3 US1 → **STOP & VALIDATE**: vend a spoke end to end (quickstart Scenario 1).
3. Phase 4 US2 → the Article VIII plan/confirm gate + clean teardown with allocation release. This is
   the smallest **constitutionally complete** shippable increment.

### Incremental delivery

4. US3 → whole-platform CLI surface. 5. US4 → registry/audit queries. 6. US5 → resilient webhook
   tracking (host-ready for spec 007). Each story is independently testable and adds value without
   breaking prior stories.

### Notes

- `[P]` = different files, no incomplete-task dependency.
- Commit after each task or logical group (repo auto-commits only after `implement`).
- Real Postgres (Testcontainers) is required wherever the ledger/registry/saga/outbox are exercised.
- No in-process `tofu`; every mutation is a dispatched workflow (Article II) — assert this in tests.
- This spec provisions **no new Azure resources**; production hosting (ACA/ingress/App Insights) is
  spec 007.
