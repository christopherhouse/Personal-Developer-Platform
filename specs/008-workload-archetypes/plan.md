# Implementation Plan: Workload Archetypes

**Branch**: `008-workload-archetypes` | **Date**: 2026-07-02 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/008-workload-archetypes/spec.md`

## Summary

Make the platform deploy actual solutions into vended spokes. Three coupled deliverables:
(1) an **archetype catalog** — a repo-managed declarative definition
(`archetypes/catalog.json`, changed only by PR) synced by the control plane into the
existing registry Postgres; the sole source of deployable truth, with caller parameters
validated against each archetype's JSON schema (JsonSchema.Net) **before** any dispatch.
(2) **workload verbs** — `IWorkloadVerbs` (plan-deploy / deploy / plan-destroy / destroy)
extending the spec-006 verb layer with zero reimplementation: same
validate → record intent → plan → confirm → dispatch → track spine, same Wolverine
lifecycle saga (new `BeginWorkloadDeploy`/`BeginWorkloadDestroy` messages, **no IPAM
step** — workloads carve no address space), same GitHub `workflow_dispatch` + `env_id`
run-name correlation, one OpenTofu state per workload
(`workloads/<sub-id>/<spoke-name>/<workload-name>`), surfaced by `pdp workload
deploy|destroy` and MCP tools `PlanWorkloadDeploy`/`ApplyWorkloadDeploy`/
`PlanWorkloadDestroy`/`DestroyWorkload` with the established token + verbatim-restatement
gate. The registry's `EnvironmentKind` gains `Workload`; spoke destroy gains an
active-workloads refusal guard (FR-021).
(3) the **first archetype** `container-app-sql` — an OpenTofu module in the platform repo
(pinned by git tag) deploying a container app into the spoke's shared ACA environment plus
an Azure SQL serverless (auto-pause) database, private by default, AVM-first, diagnostics
to the shared Log Analytics workspace — plus the **workload template repo** (.NET 10
minimal API skeleton) whose CI consumes the platform's first reusable (`workflow_call`)
container build/push workflow.

Cross-spec stack changes required (recorded, precedent: spec-007 subnet + PR #17 lock
re-scope): `infra/spoke` vends a delegated `/27` ACA subnet and a shared per-spoke ACA
managed environment; `infra/platform-dns` adds the `privatelink.database.windows.net`
zone; `infra/fabric` surfaces it in `shared_dns_zone_ids`.

## Technical Context

**Language/Version**: .NET 10 (LTS, `global.json`-pinned) for all platform code; OpenTofu
1.11.x (`.opentofu-version`) for all IaC. No Python (constitution).

**Primary Dependencies** (pins from `Directory.Packages.props` unless noted):
- Verb layer: Wolverine 6.12.0 (+ Postgresql/EFCore), EF Core 10.0.9 + Npgsql 10.0.2 +
  EFCore.NamingConventions 10.0.1, FluentValidation 12.1.1, Octokit 14.0.0.
- **NEW**: `JsonSchema.Net` (json-everything, latest 7.x — pin exact version at
  implement; draft 2020-12 default) for per-archetype parameter-schema evaluation.
  Already sanctioned by `docs/tech-stack.md`; not yet referenced by any project.
- CLI: System.CommandLine 2.0.9. MCP: ModelContextProtocol.AspNetCore 0.9.0-preview.2.
- IaC: azurerm 4.x, AVM modules — reuse `avm-res-app-containerapp` 0.9.0,
  `avm-res-app-managedenvironment` 0.4.0 (NOT 0.5.0 — its submodules require
  Terraform ~>1.12, incompatible with OpenTofu 1.11.x; same trap as spec 007),
  `avm-res-network-privatednszone` 0.5.0; **NEW adoption** `avm-res-sql-server` 0.2.1
  (databases incl. serverless SKUs `GP_S_Gen5_*` + `auto_pause_delay_in_minutes` +
  `min_capacity`, private endpoints, diagnostic settings, Entra-only auth; azurerm
  ~>4.26) — Article V smoke-validation under pinned OpenTofu required before reliance.

**Storage**: existing control-plane Postgres (Flexible Server, private, Entra-only) —
schema `registry` gains catalog tables (`archetypes`, `archetype_versions`,
`catalog_syncs`) and a `workloads` detail table; one new EF migration also extends the
`EnvironmentKind` enum with `workload`. OpenTofu state: existing foundations storage
account (`stpdpwus3statejqyq`/`tfstate`), partial backend config, key
`workloads/<sub-id>/<spoke-name>/<workload-name>` supplied at `tofu init`.

**Testing**: xUnit + Shouldly + NSubstitute; Testcontainers.PostgreSql + Respawn for
registry/catalog integration (natural-key + immutability constraints are
database-enforced); WireMock.Net for GitHub dispatch; `WebApplicationFactory` for MCP
tool surface. `tofu fmt/validate` + a live smoke-validation of `avm-res-sql-server`
under OpenTofu 1.11 (Article V).

**Target Platform**: control plane already hosted on ACA (spec 007) — api/mcp images
gain the catalog file + sync service; workloads land on a per-spoke ACA environment
(consumption profile, scale-to-zero) + Azure SQL serverless in the target subscription.
Live region: westus3.

**Project Type**: multi-project .NET solution + OpenTofu stacks + GitHub Actions
execution plane + one new external GitHub template repository.

**Performance Goals**: schema validation + catalog lookup adds no perceptible latency to
plan verbs (<100 ms, in-process against local registry data). Deploy end-to-end bounded
by execution-plane runtime (~10–15 min for first ACA env + SQL provision), tracked
asynchronously exactly like spoke vend (dispatch-and-return on the chat surface).

**Constraints**: no reimplementation of the spec-006 verb spine or spec-007 hosting; no
new verb machinery beyond the workload domain; chat/CLI can never mutate the catalog
(clarify 2026-07-02); workloads carve no address space and own no egress; no public
endpoint unless the parameter opts in; prohibited deps stay prohibited (JsonSchema.Net is
explicitly sanctioned). CLI live acceptance uses the established transient-ACA-job
path to reach the private Postgres (laptop cannot).

**Scale/Scope**: single owner, one region live, one archetype, expected O(1–10)
workloads. ~4 new registry tables, ~1 saga extension, 4 verbs, 4 MCP tools, 2 CLI
commands, 2 dispatch workflows + 1 reusable workflow, 1 archetype module, 1 template
repo, 3 touched infra stacks.

## Constitution Check

*GATE: evaluated pre-Phase-0 and re-checked post-Phase-1 design. Constitution v1.1.0.*

| Article | Gate | Verdict |
|---|---|---|
| I — Infrastructure is Code | All workload mutations via the archetype OpenTofu module executed by dispatched workflows; catalog sync writes registry rows only (intent, not Azure state). | **PASS** |
| II — AI Calls Verbs | New capability = new typed verbs (`workload deploy/destroy`) consumed by CLI + MCP; catalog is repo-managed — chat/CLI *cannot* alter deployable truth (tighter than baseline). No runtime IaC generation: modules are pinned by git tag, parameters are schema-validated data. | **PASS** |
| III — Tagged, Tracked | Workload RG carries `pdp-managed`, `pdp-deployed-by`, `pdp-workload`, `pdp-env` (+ spoke context via inventory classifier); `ListWorkloadEnvironments` lights up with **no inventory code change** (classifier already reads these keys). Registry answers intent; ARG answers deployed. | **PASS** |
| IV — Destroyable by Design | `workload destroy` = one verb tearing the isolated workload state (RG + app + SQL + PE); spoke untouched; FR-021 guard refuses spoke destroy while workloads survive (names them). Teardown is an acceptance criterion (SC-007). | **PASS** |
| V — AVM-First | Reuses pinned AVM modules; new adoption `avm-res-sql-server` 0.2.1 smoke-validated under OpenTofu 1.11 before reliance (explicit task). Only raw `azurerm` resources: UAMI + role assignments (same README-justified exception as spec 007). | **PASS** (smoke task mandatory) |
| VI — No Address Space Without Allocation | Workloads allocate **nothing**: the new `/27` ACA subnet is carved *inside the spoke's already-ledgered block* by the spoke stack (identical to spec-004 subnet carving); no new ledger rows, no releases. | **PASS** |
| VII — Hub Owns Egress | ACA subnet gets the spoke's existing `0.0.0.0/0 → hub firewall` route table (workload-profiles env supports UDR — that's why the env type is mandatory); SQL is private-endpoint only. Risk: hub firewall must permit ACA platform FQDNs — verified at live acceptance, any rules land in the fabric policy by PR (see research R10). | **PASS** |
| VIII — Plan Before Apply, Confirm Before Destroy | Same two-phase gate: `PlanWorkloadDeploy` → token; `DestroyWorkload` requires token + verbatim workload-name restatement (`ConfirmationGuard.RequireMatch` + the workflow-side `destroy-confirm` input gate — both layers, as spokes do). | **PASS** |
| IX — Secure and Cheap | Private by default (internal ingress, PE-only SQL, opt-in public endpoint made visible in the plan); serverless auto-pause SQL (compute → $0 paused), consumption-profile ACA (scale-to-zero, $0 idle), Basic everything. | **PASS** |
| X — Specs Before Code | This flow; backlog deps (specs 4, 6) merged. | **PASS** |
| XI — Observable by Design | ACA env + SQL server/db diagnostic settings → shared Log Analytics workspace (data-source-by-name pattern, per `platform-observability`'s anticipation comment); verb telemetry rides the existing `ControlPlaneTelemetry`. Acceptance criterion SC-006. | **PASS** |

**Additional constraints**: no prohibited deps introduced (JsonSchema.Net is
tech-stack-sanctioned); OpenTofu-only; control plane dispatches / GitHub Actions
executes; division of truth preserved (Postgres = intent + catalog projection, ARG =
deployed). **Post-Phase-1 re-check: PASS — no deviations; Complexity Tracking empty.**

## Project Structure

### Documentation (this feature)

```text
specs/008-workload-archetypes/
├── plan.md              # This file
├── research.md          # Phase 0 — decisions R1–R11
├── data-model.md        # Phase 1 — catalog + workload registry model
├── quickstart.md        # Phase 1 — validation scenarios
├── contracts/
│   ├── workload-verbs.md      # IWorkloadVerbs + MCP tools + CLI commands
│   ├── archetype-catalog.md   # catalog.json format, lifecycle, sync semantics
│   └── execution-plane.md     # workflows, state keys, archetype module contract
└── tasks.md             # Phase 2 (/speckit-tasks — NOT created by /speckit-plan)
```

### Source Code (repository root)

```text
src/
├── Pdp.ControlPlane.Registry/          # EXTEND: catalog + workload entities
│   ├── Entities/{Archetype,ArchetypeVersion,CatalogSync,WorkloadDetails}.cs
│   ├── Entities/Enums.cs               # EnvironmentKind + Workload
│   ├── Catalog/{ICatalogStore,CatalogStore,CatalogSyncService,CatalogDefinition}.cs
│   ├── Lifecycle/LifecycleMessages.cs  # + BeginWorkloadDeploy/BeginWorkloadDestroy
│   └── Migrations/                     # + 2026xxxx_WorkloadCatalog migration
├── Pdp.ControlPlane.Verbs/             # EXTEND
│   ├── Handlers/{IWorkloadVerbs,WorkloadVerbs}.cs
│   ├── Handlers/SpokeVerbs.cs          # + FR-021 active-workloads destroy guard
│   ├── Model/{Requests,Results}.cs     # + WorkloadDeployRequest etc.
│   └── Validation/WorkloadDeployValidator.cs  # shape; schema eval in WorkloadVerbs
├── Pdp.ControlPlane.Api/               # EXTEND: host CatalogSyncService (sole writer)
├── Pdp.Cli/Commands/WorkloadCommand.cs # NEW: pdp workload deploy|destroy
└── Pdp.Mcp/Tools/WorkloadTools.cs      # NEW: 4 tools; Confirm/ConfirmationOperation +2

tests/
├── Pdp.ControlPlane.Registry.Tests/    # catalog sync + immutability (Testcontainers)
├── Pdp.ControlPlane.Verbs.Tests/       # workload verbs, schema rejection, FR-021
├── Pdp.Cli.Tests/ · Pdp.Mcp.Tests/     # command + tool surface

archetypes/
├── catalog.json                        # NEW: the declarative catalog (baked into api image)
└── container-app-sql/                  # NEW: first archetype module (tagged releases)
    ├── main.tf · variables.tf · outputs.tf · backend.tf · README.md

infra/
├── spoke/          # EXTEND: default subnets + delegated /27 'aca'; shared ACA env; outputs
├── platform-dns/   # EXTEND: + privatelink.database.windows.net
└── fabric/         # EXTEND: shared_dns_zone_ids += sql

.github/workflows/
├── workload-deploy.yml                 # NEW (modeled on spoke-vend.yml + tag checkout)
├── workload-destroy.yml                # NEW (modeled on spoke-destroy.yml)
├── reusable-container-build.yml        # NEW: first workflow_call workflow
└── controlplane-host-images.yml        # EXTEND: paths += archetypes/catalog.json

<external> pdp-workload-template        # NEW GitHub template repo (.NET 10 minimal API)
```

**Structure Decision**: no new .NET project — workload verbs are a domain extension of
the existing four-project control-plane layout (Registry/Verbs/Api + surfaces), exactly
how fabric verbs sit beside spoke verbs today. The archetype module lives at repo root
`archetypes/` (glossary: the platform repo owns "fabric/archetype OpenTofu modules"),
not under `infra/` — `infra/` stacks are platform-owned singletons/parameterized stacks
applied by platform CI, while `archetypes/*` are versioned products stamped per
workload at a pinned tag.

## Complexity Tracking

No constitution violations to justify. The three cross-spec stack edits
(`infra/spoke`, `infra/platform-dns`, `infra/fabric`) follow the recorded spec-007
precedent (subnet declared in the VNet-owning stack) and are itemized in research
R5/R6 with rationale.
