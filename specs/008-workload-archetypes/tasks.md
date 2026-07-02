# Tasks: Workload Archetypes

**Input**: Design documents from `/specs/008-workload-archetypes/`

**Prerequisites**: plan.md, spec.md, research.md (R1–R11), data-model.md,
contracts/{workload-verbs,archetype-catalog,execution-plane}.md, quickstart.md

**Tests**: included — constitution testing discipline (Testcontainers for
database-enforced catalog/registry behavior) + spec acceptance scenarios (schema
rejection, guards) demand them.

**Organization**: grouped by user story; each story is independently deliverable and
testable. Live-acceptance tasks map 1:1 to quickstart scenarios.

## Format: `[ID] [P?] [Story] Description`

---

## Phase 1: Setup

**Purpose**: package pins, scaffolding, and the binding-doc edits the constitution
requires before use.

- [X] T001 Add `JsonSchema.Net` (latest 7.x, exact pin) to `Directory.Packages.props`; add `PackageReference` to `src/Pdp.ControlPlane.Verbs/Pdp.ControlPlane.Verbs.csproj` and `src/Pdp.ControlPlane.Registry/Pdp.ControlPlane.Registry.csproj` — pinned **7.4.0** (latest stable 7.x on NuGet, 2026-07-02); `dotnet build` green
- [X] T002 [P] Create `archetypes/README.md`: authoring conventions, release runbook (edit `catalog.json` + create tag `archetype/<name>/v<semver>` in the same PR), parameter-schema conventions per `contracts/archetype-catalog.md`
- [X] T003 [P] Update `docs/glossary.md`: extend **Managed unit** to include workloads (FR-014); cross-check **Archetype catalog** wording matches the repo-managed model (git source of change, Postgres projection) — also updated the `env_id` parenthetical for consistency
- [X] T004 [P] Extend `.github/workflows/controlplane-host-images.yml` path filter with `archetypes/catalog.json` and add `COPY archetypes/catalog.json` to the api image `src/Pdp.ControlPlane.Api/Dockerfile` (R1) — NOTE: api image build fails until T013 authors `archetypes/catalog.json` (CI builds only on push to main, so harmless within this branch)

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: registry model + catalog projection + saga extension every story rides on.

**⚠️ CRITICAL**: no user story work until this phase completes.

- [X] T005 Add `Workload` to `EnvironmentKind` in `src/Pdp.ControlPlane.Registry/Entities/Enums.cs` (persisted `"workload"`; update the "deferred to spec 008" comment) — also added `ArchetypeStatus` + `CatalogSyncOutcome` enums and updated the stale comment in `Environment.cs`
- [X] T006 [P] Create catalog entities `Archetype`, `ArchetypeVersion`, `CatalogSync` in `src/Pdp.ControlPlane.Registry/Entities/` per data-model.md (statuses, content_hash, jsonb parameter_schema)
- [X] T007 [P] Create `WorkloadDetails` entity in `src/Pdp.ControlPlane.Registry/Entities/WorkloadDetails.cs` (env_id PK/FK, spoke ref, archetype name+version, pdp_env, parameters jsonb)
- [X] T008 Configure new tables in `src/Pdp.ControlPlane.Registry/RegistryDbContext.cs`: keys, jsonb columns, `(spoke_subscription, spoke_name)` index on workloads, cascade env_id FK (depends T005–T007) — plus composite Restrict FK `workloads → archetype_versions` (database backstop for the FR-005 stamp)
- [X] T009 Add EF migration `WorkloadCatalog` in `src/Pdp.ControlPlane.Registry/Migrations/` (`dotnet ef migrations add` against `RegistryDbContextFactory`); verify it applies locally (Aspire Postgres) AND determine/document the live application path — the laptop cannot reach the private Postgres, so live = transient ACA job running `pdp migrate` (R11) or confirmed api-startup migration; record which in the task closure (pre-req for T032) — **CLOSURE**: `20260702185823_WorkloadCatalog` generated; applies + reverts cleanly on real Postgres (SchemaMigrationTests extended to the 4 new tables, green). Live path = **transient ACA job running `pdp migrate`** (the existing `MigrateCommand` applies ipam + registry migrations; the api host does NOT migrate at startup) — schedule inside T032 before the new image rolls out
- [X] T010 Implement `CatalogDefinition` file model + parser/validator in `src/Pdp.ControlPlane.Registry/Catalog/CatalogDefinition.cs`: shape rules, every `parameterSchema` must parse as valid draft 2020-12 (JsonSchema.Net), canonical content hashing (per-version + whole-file) — canonicalization in `CanonicalJson.cs` (sorted keys, no whitespace) so cosmetic file edits never trip immutability; schemas validated by parse + draft 2020-12 **meta-schema** evaluation
- [X] T011 Implement `ICatalogStore`/`CatalogStore` in `src/Pdp.ControlPlane.Registry/Catalog/`: resolve active archetype + newest active version (SemVer order), lookup stamped version, list-for-display — SemVer 2.0 precedence in `SemVerComparer.cs` (v1.10.0 > v1.9.0 proven in tests)
- [X] T012 Implement `CatalogSyncService` (hosted service) in `src/Pdp.ControlPlane.Registry/Catalog/CatalogSyncService.cs` + register in `src/Pdp.ControlPlane.Api/Program.cs` ONLY (sole writer): whole-file hash short-circuit, all-or-nothing upsert, immutability rejection is sync-fatal + keeps prior projection, `catalog_syncs` audit row (R1/R2) — sync core split into testable `CatalogSynchronizer.cs`; hosted service resolves the baked file (container) or repo root (local dev) and never blocks boot
- [X] T013 Author the seed catalog `archetypes/catalog.json`: `container-app-sql` v1.0.0 entry with the full parameter schema from `contracts/archetype-catalog.md` — a test syncs the real repo file to keep it permanently valid
- [X] T014 Add `BeginWorkloadDeploy`/`BeginWorkloadDestroy` (+ `WorkloadDispatchInputs`) to `src/Pdp.ControlPlane.Registry/Lifecycle/LifecycleMessages.cs` and handle them in `EnvironmentSaga` — same flow as spoke messages minus IPAM allocate/release; dispatch targets `workload-deploy.yml`/`workload-destroy.yml` (RunCompleted/RunFailed already release only for `Kind == Spoke`, so workloads get no release on any path)
- [X] T015 [P] Testcontainers integration tests for catalog sync in `tests/Pdp.ControlPlane.Registry.Tests/CatalogSyncTests.cs`: first-sync insert, no-change short-circuit, retire/reactivate, **immutability rejection preserves previous projection**, invalid-file rejection, audit rows (US4 data layer proven here) — 8 tests incl. cosmetic-reformat safety + seed-file validity
- [X] T016 [P] Testcontainers tests for workload managed-unit rows in `tests/Pdp.ControlPlane.Registry.Tests/WorkloadRegistryTests.cs`: natural-key idempotency for kind=workload, WorkloadDetails cascade, migration round-trip — 5 tests incl. kind-in-natural-key coexistence and the Restrict FK protecting stamped versions

**Checkpoint**: `dotnet build && dotnet test` green — user stories can begin.

---

## Phase 3: User Story 1 — Deploy a workload from the catalog into a vended spoke (P1) 🎯 MVP

**Goal**: end-to-end deploy via CLI and chat: schema-validated parameters → plan →
confirm → dispatched run → running, tagged, private workload in the spoke.

**Independent Test**: with the seeded catalog + one live spoke, deploy from the CLI and
from chat; workload resources exist in the spoke with full tags; invalid parameters are
rejected before dispatch; run recorded and correlated (quickstart Scenarios 3–6).

### Verb layer

- [X] T017 [P] [US1] Add `WorkloadDeployRequest` to `src/Pdp.ControlPlane.Verbs/Model/Requests.cs` and `WorkloadParameterValidationException` (+`ParameterViolation`), `ArchetypeNotDeployableException` to `src/Pdp.ControlPlane.Verbs/VerbExceptions.cs` per `contracts/workload-verbs.md` — also `WorkloadParametersChangedException` + `SpokeNotActiveException` (US1-AS5 inactive-spoke refusal, distinct from `EnvironmentNotFoundException`)
- [X] T018 [P] [US1] Create `WorkloadDeployValidator` (FluentValidation) in `src/Pdp.ControlPlane.Verbs/Validation/WorkloadDeployValidator.cs` — name/pdp-env rules delegate to `TagSchema.IsValidResourceName`/`IsValidEnvName` (single source of truth), archetype regex `^[a-z0-9-]{1,32}$`
- [X] T019 [US1] Implement parameter-schema evaluation in `src/Pdp.ControlPlane.Verbs/Validation/ParameterSchemaEvaluator.cs`: JsonSchema.Net `OutputFormat.List` → `ParameterViolation` list (path, keyword, message) (depends T017) — document-level failures report path `(root)`
- [X] T020 [US1] Implement `IWorkloadVerbs`/`WorkloadVerbs` (PlanDeployAsync/DeployAsync) in `src/Pdp.ControlPlane.Verbs/Handlers/`: validation order shape→catalog→schema→spoke(Active)→intent (R3); build dispatch inputs (archetype_path/archetype_ref/parameters_json/pdp_env); persist `WorkloadDetails` with stamped version; repeat-deploy semantics per `contracts/workload-verbs.md` (identical parameters → idempotent convergence; differing parameters/archetype → `WorkloadParametersChangedException`); register in `ServiceCollectionExtensions.cs`; telemetry via `ControlPlaneTelemetry` (depends T014, T017–T019) — details persistence via new `IEnvironmentRegistry.UpsertWorkloadDetailsAsync`/`FindWorkloadDetailsAsync`; `ICatalogStore` now registered in `AddControlPlaneVerbs` (read-side only); repeat-compare re-canonicalizes both sides (jsonb reorders keys); confirm path rebuilds apply inputs from the STAMPED details row; PlanDestroy/Destroy are explicit `NotSupportedException` stubs until US2 (T036)
- [X] T021 [US1] Verb tests in `tests/Pdp.ControlPlane.Verbs.Tests/WorkloadVerbsTests.cs`: schema violation → no intent row + no dispatch (AS3/SC-003), unknown vs retired archetype refusal (AS4), missing/inactive spoke refusal (AS5), newest-active-version resolution + stamped version persisted, repeat deploy with identical parameters idempotent vs differing parameters refused (`WorkloadParametersChangedException`), plan→confirm happy path (Testcontainers + WireMock dispatch) — 7 tests green incl. SemVer v1.10.0>v1.9.0 stamp, cosmetic-key-reorder convergence, PlanNotReady before confirm; dispatcher faked at the `IWorkflowDispatcher` seam (the suite's established pattern; WireMock reserved for artifact-capture flows already covered by PlanConfirmTests)

### Execution plane + archetype

- [X] T022 [P] [US1] Create `.github/workflows/workload-deploy.yml` per `contracts/execution-plane.md`: run-name correlation, tag checkout (`ref: inputs.archetype_ref`), `-chdir` module exec, state key `workloads/<sub>/<spoke>/<workload>`, plan artifact upload, concurrency group, notify-sre-agent
- [X] T023 [P] [US1] Author archetype module `archetypes/container-app-sql/` (`main.tf`, `variables.tf`, `outputs.tf`, `backend.tf`, `README.md`): RG + UAMI (+conditional AcrPull), container app (AVM 0.9.0, spoke shared env by remote state, `ingress.external = parameters.publicEndpoint` default false), SQL server+db (AVM `avm-res-sql-server` 0.2.1, Entra-only, UAMI admin, PE + zone group, serverless GP_S_Gen5_1), diagnostics → shared LA, full tag set incl. `pdp-workload`+`pdp-env`; README records UAMI raw-resource justification + UAMI-as-admin decision (R6) — plus `versions.tf` (dual-provider) + `data.tf`; app env carries password-less `ConnectionStrings__Sql` + `AZURE_CLIENT_ID`; `tofu init -backend=false && tofu validate` clean under OpenTofu 1.11 at the pinned module versions (incl. per-database `diagnostic_settings` on avm-res-sql-server 0.2.1); live apply/destroy smoke = T027
- [X] T024 [P] [US1] Add `privatelink.database.windows.net` zone to `infra/platform-dns/main.tf` (AVM privatednszone 0.5.0, key `sql`) — + `shared_dns_zone_ids` output entry
- [X] T025 [US1] Surface the sql zone in `infra/fabric`: extend `data.azurerm_private_dns_zone.shared` + `shared_dns_zone_ids` output in `infra/fabric/outputs.tf` (depends T024) — one edit to `local.shared_dns_zones` (`sql` key); the data source, hub link, and output are for-expressions over it
- [X] T026 [US1] Extend `infra/spoke`: default `subnets` = `workload` (/25) + `aca` (/27, delegation `Microsoft.App/environments`) in `variables.tf`; shared ACA env `cae-pdp-<region>-<spoke_name>` (AVM managedenvironment **0.4.0**, workload-profiles, consumption-only, external, VNet-integrated, diagnostics → shared LA) in `main.tf`; `spoke_aca_environment_id` output (R5) — subnet sizes are newbits-relative (a /24 spoke yields /25+/27); shared-LA lookup by name via new `observability_*` vars (control-plane-host precedent); `tofu validate` clean
- [X] T027 [US1] Article V smoke-validation of `avm-res-sql-server` 0.2.1 under OpenTofu 1.11.x (throwaway RG: init/plan/apply/destroy of minimal serverless config); record result in `archetypes/container-app-sql/README.md` (quickstart Scenario 2 — **gate for T034**) — **CLOSURE 2026-07-02**: owner-run local one-off (local state, R9's sanctioned exception), RG `rg-pdp-westus3-smoke-sql` in the platform sub; apply AND destroy clean under OpenTofu 1.11.6 — serverless `GP_S_Gen5_1`/`min_capacity`/auto-pause + Entra-only admin (no `administrator_login`) + `public_network_access_enabled=false` all accepted at the 0.2.1 pin; result recorded in the archetype README; T034 unblocked

### Surfaces

- [X] T028 [P] [US1] Create `src/Pdp.Mcp/Tools/WorkloadTools.cs` with `PlanWorkloadDeploy`/`ApplyWorkloadDeploy` (token issue/redeem/consume, schema violations returned as data, no token on failure); add `WorkloadDeploy` to `ConfirmationOperation` in `src/Pdp.Mcp/Confirm/` (depends T020) — `WorkloadDestroy` enum member added alongside (contract says +2; US2 wires it); new `McpWorkloadPlanResult` carries token+plan OR `ParameterViolations` as data; registered via `.WithTools<WorkloadTools>` in `Program.cs`
- [X] T029 [P] [US1] Create `src/Pdp.Cli/Commands/WorkloadCommand.cs` `deploy` subcommand (`--param`/`--parameters-file` merge, plan→prompt→apply flow, exit codes incl. 2 with per-parameter lines); wire into `src/Pdp.Cli/Program.cs` (depends T020) — `--param` values parse as JSON scalars with string fallback; file wins on conflict; `destroy` subcommand lands with US2 (T040)
- [X] T030 [P] [US1] MCP tool tests in `tests/Pdp.Mcp.Tests/WorkloadToolsTests.cs`: plan issues token, apply requires verbatim name, schema-invalid → violations + no token — 8 tests green incl. mismatch-does-not-burn-token, forged-token, malformed-JSON pre-verb rejection, non-owner refusal
- [X] T031 [P] [US1] CLI tests in `tests/Pdp.Cli.Tests/WorkloadCommandTests.cs`: param parsing/merge, exit codes, destroy-style prompt skipped (`--yes` deploy only) — 9 tests green incl. per-parameter stderr lines (`<path>: <message>`), file-wins merge, exit 2/3 mapping

### Live acceptance (US1)

- [ ] T032 [US1] Merge/apply infra edits (`iac-plan`/`iac-apply` for dns+fabric); re-vend the test spoke on the updated stack via chat; verify aca subnet + shared env + sql zone link (quickstart Scenario 3; storage-account overnight-policy check first). Environment pre-checks: (a) apply the T009 migration via its documented live path, then confirm the `uami-api`/`uami-mcp` Postgres roles can read/write the four new registry tables (grants/default privileges — PR #29 precedent); (b) confirm the CI OIDC identity can create role assignments on the platform ACR scope (needed for the archetype's conditional AcrPull; spec-004 cross-sub-grant precedent) — pre-grant or document a bootstrap step in `archetypes/container-app-sql/README.md` if not
- [ ] T033 [US1] Live: schema-invalid + unknown-archetype deploys via chat rejected before dispatch — no token, no intent row, no GitHub run (Scenario 4, SC-003)
- [ ] T034 [US1] Live: deploy `container-app-sql` via chat (plan → token+verbatim → apply → Active); verify RG/tags/private ingress/SQL PE/state key; verify smallest-viable tiers (SC-005: SQL `GP_S_Gen5_1` serverless, ACA consumption profile only, no dedicated profiles) and record that the spoke env's public IP is env-level, not a workload endpoint (workload ingress internal — R5); exercise R10 firewall contingency if image pull fails (fabric policy PR) (Scenario 5, SC-001)
- [ ] T035 [US1] Live: deploy second workload via `pdp workload deploy` through the transient ACA job (R11); verify identical behavior + `--param cpu=3` exits 2 (Scenario 6, SC-002)

**Checkpoint**: MVP — workloads deploy from both surfaces.

---

## Phase 4: User Story 2 — Destroy a workload cleanly (P2)

**Goal**: single-verb teardown with Article VIII double gate; spoke untouched; FR-021
spoke-destroy guard.

**Independent Test**: destroy a deployed workload from chat with wrong-then-right
restatement; spoke `tofu plan` shows no changes; spoke destroy refused while workloads
survive (quickstart Scenario 9).

- [ ] T036 [US2] Implement `WorkloadVerbs.PlanDestroyAsync`/`DestroyAsync` in `src/Pdp.ControlPlane.Verbs/Handlers/WorkloadVerbs.cs`: `ConfirmationGuard.RequireMatch` before dispatch, stamped `archetype_ref` from `WorkloadDetails`, `destroy-confirm` input = env name
- [ ] T037 [P] [US2] Add FR-021 guard to `SpokeVerbs.PlanDestroyAsync`/`DestroyAsync` in `src/Pdp.ControlPlane.Verbs/Handlers/SpokeVerbs.cs` + `SpokeHasActiveWorkloadsException` (lists survivor names) in `VerbExceptions.cs`
- [ ] T038 [P] [US2] Create `.github/workflows/workload-destroy.yml`: `destroy-confirm != workload_name` gate step, stamped-tag checkout, plan-destroy artifact, shared concurrency group with deploy
- [ ] T039 [P] [US2] Add `PlanWorkloadDestroy`/`DestroyWorkload` to `src/Pdp.Mcp/Tools/WorkloadTools.cs` + `WorkloadDestroy` `ConfirmationOperation` (depends T036)
- [ ] T040 [P] [US2] Add `destroy` subcommand to `src/Pdp.Cli/Commands/WorkloadCommand.cs` (`--confirm` restates name, no `--yes` bypass) (depends T036)
- [ ] T041 [US2] Tests: destroy verb restatement mismatch refused + token intact (MCP), FR-021 guard names survivors, destroy uses stamped ref — in `tests/Pdp.ControlPlane.Verbs.Tests/WorkloadDestroyTests.cs` + `tests/Pdp.Mcp.Tests/WorkloadToolsTests.cs`
- [ ] T042 [US2] Live: spoke-destroy refused naming survivors → workload destroy via chat (wrong restatement refused, verbatim succeeds) → RG+state gone, spoke plan no-changes, run correlated (Scenario 9.1–9.2, SC-007)

**Checkpoint**: full deploy→destroy lifecycle proven live.

---

## Phase 5: User Story 3 — Workload environments appear in inventory (P3)

**Goal**: `ListWorkloadEnvironments` returns live, grouped, non-empty answers (zero
inventory code change expected — tags do the work).

**Independent Test**: with one deployed workload tagged `pdp-env=dev`, the chat
inventory answer groups it under `dev` with spoke + subscription; after destroy it
disappears (quickstart Scenario 7).

- [ ] T043 [P] [US3] Classifier unit tests in `tests/Pdp.ControlPlane.Inventory.Tests/WorkloadEnvironmentTests.cs`: workload RG tag set → `EnvironmentView`/`WorkloadItem` grouping, missing `pdp-env` → conformance finding (guards the zero-change assumption)
- [ ] T044 [US3] Live: `ListWorkloadEnvironments` non-empty grouped under `dev` (both US1 workloads) + `ShowEnvironment` resolves `(workload, sub, name)`; single query/turn (Scenario 7, SC-004)
- [ ] T045 [US3] Live: after the US2 destroy, the workload no longer appears; environment vanishes when last member destroyed (US3-AS2)

---

## Phase 6: User Story 4 — Catalog lifecycle (P4)

**Goal**: register/version/retire through PR-only catalog changes; pinned versions
never move (sync mechanics already proven by T015 — this phase proves verb behavior +
the live pipeline).

**Independent Test**: append v1.1.0 → existing workload still reports v1.0.0, fresh
deploy resolves v1.1.0; retire → new deploys refused, destroy still works (quickstart
Scenario 8).

- [ ] T046 [US4] Verb-level lifecycle tests in `tests/Pdp.ControlPlane.Verbs.Tests/CatalogLifecycleTests.cs`: retired refusal distinct from unknown, newest-active resolution across versions, stamped version survives new registration (US4-AS1..AS3)
- [ ] T047 [US4] Live: PR appending `container-app-sql` v1.1.0 (catalog entry + git tag) → image redeploy → sync applied; deployed workload still reports v1.0.0; fresh plan resolves v1.1.0 (Scenario 8, SC-008)
- [ ] T048 [US4] Live: PR flipping `status: retired` → new deploy refused with "retired"; destroy of a stamped workload still succeeds; `catalog_syncs` audits both changes (US4-AS2..AS4, FR-006)

---

## Phase 7: User Story 5 — Template repo + reusable workflow (P5)

**Goal**: stamped app repos get working CI from day one via the platform's first
`workflow_call` workflow.

**Independent Test**: stamp a repo, bootstrap once, push — CI green, image in the
platform ACR (quickstart Scenario 11).

- [ ] T049 [P] [US5] Create `.github/workflows/reusable-container-build.yml` (`workflow_call`: image_repository/dockerfile/context; OIDC login → `az acr login` → build/push `:$GITHUB_SHA`+`:latest`) per `contracts/execution-plane.md`
- [ ] T050 [US5] Create the external `pdp-workload-template` repository: .NET 10 minimal API (health endpoint + SQL-backed route via `Microsoft.Data.SqlClient` managed-identity auth), Dockerfile, xUnit smoke test, CI workflow consuming T049, README with the one-time bootstrap runbook (federated credential + AcrPush + `AZURE_*` variables) (R8)
- [ ] T051 [US5] Mark `pdp-workload-template` as a GitHub template repo; stamp a test repo; perform the one-time bootstrap per its README
- [ ] T052 [US5] Live: stamped repo CI green on first push, image lands in platform ACR; optionally deploy a new workload (or destroy→deploy an existing one — no in-place reconfiguration) using the stamped image (managed-identity pull, no credentials) (Scenario 11, SC-009)

---

## Phase 8: Polish & Cross-Cutting

- [ ] T053 [P] Update `docs/spec-backlog.md` status for spec 8 (+ note the first reusable workflow exists)
- [ ] T054 [P] Record the exact pinned `JsonSchema.Net` version + AVM `avm-res-sql-server` adoption in `docs/tech-stack.md`
- [ ] T055 Live: diagnostics verification — spoke ACA env + SQL server/db logs+metrics in the shared Log Analytics workspace; `env_id`-correlated deploy/destroy telemetry in App Insights (Scenario 10, SC-006, Article XI)
- [ ] T056 Live: full teardown sweep — remaining workloads destroyed, test spoke destroyed (guard now passes), Scenario 2 smoke RG deleted, `WhatsDeployed` clean, no orphaned state objects (Article IV)
- [ ] T057 `dotnet format` + `tofu fmt`/`tofu validate` across touched stacks + module; CI (`dotnet.yml`, `iac-plan`) green
- [ ] T058 Walk `specs/008-workload-archetypes/quickstart.md` end to end; check off scenarios; record deferrals (if any) in `docs/spec-backlog.md`

---

## Dependencies & Execution Order

### Phase Dependencies

- **Setup (P1)** → **Foundational (P2)** → user stories. Foundational blocks everything.
- **US1 (Phase 3)**: only on Foundational. **MVP.**
- **US2 (Phase 4)**: code depends on T020 (WorkloadVerbs class exists); live tasks depend on a US1-deployed workload.
- **US3 (Phase 5)**: T043 independent; live tasks need US1 (T044) and US2 (T045) outcomes.
- **US4 (Phase 6)**: T046 needs Foundational only; live tasks need US1 pipeline.
- **US5 (Phase 7)**: fully independent of US1–US4 after Setup (can run any time; only T052's optional redeploy touches US1).
- **Polish (Phase 8)**: T053/T054/T057 anytime late; T055/T056/T058 after live stories.

### Parallel Opportunities

- Setup: T002/T003/T004 in parallel after T001.
- Foundational: T006+T007 parallel; T015+T016 parallel after T012–T014.
- US1: three tracks in parallel after T020 — surfaces (T028–T031), execution plane/IaC (T022–T027), verb tests (T021). T022/T023/T024/T026 are all different files.
- US5 (T049–T052) can run entirely in parallel with US2–US4.

### Parallel Example: User Story 1

```text
# After T020, launch concurrently:
Task: T022 workload-deploy.yml          Task: T028 MCP WorkloadTools (deploy)
Task: T023 archetypes/container-app-sql Task: T029 CLI WorkloadCommand (deploy)
Task: T024 platform-dns sql zone        Task: T021 WorkloadVerbsTests
```

---

## Implementation Strategy

**MVP first**: Phases 1–3 (T001–T035) deliver the headline capability — deploy a real
solution into a spoke from chat and CLI. Stop, validate Scenarios 3–6, then increment.

**Incremental delivery**: US2 (safe teardown) is the natural second increment — it
completes Article IV and unblocks the destroy halves of US3/US4 live checks. US3/US4
are mostly validation weight. US5 can ship any time, even before US1's live tasks
(useful: T052's image feeds T034/T035 nicely).

**Live-task sequencing note**: T032 (infra apply + spoke re-vend) is the only
environment mutation gate — schedule it once, then T033–T035, T042, T044–T045,
T047–T048, T052, T055–T056 run against that environment in order.
