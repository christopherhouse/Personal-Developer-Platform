# Implementation Plan: Action Layer — Control Plane

**Branch**: `006-control-plane` | **Date**: 2026-06-17 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/006-control-plane/spec.md`

## Summary

Build PDP's **control plane** — the .NET 10 service that turns owner intent into **validated,
dispatched, and tracked** infrastructure operations, wrapping specs 002–005 behind **typed verbs**
exposed through the **`pdp` CLI** so *"deploy me a spoke"* is one command. The control plane
**dispatches; it never executes IaC** (Article II): each mutating verb validates → allocates address
space from the IPAM ledger → records intent in Postgres → **dispatches a GitHub Actions workflow**
(GitHub App + `workflow_dispatch`, OIDC in CI) → tracks it to completion correlated by `env_id`. Every
mutation shows its **plan before apply**; every destroy requires **explicit confirmation** (Article
VIII).

This spec **closes two deferrals**: **Gate-G1** — spoke address space is now allocated **live by size
from the IPAM ledger at vend** (the control plane reaches the private Postgres ledger CI cannot),
released on destroy, so `spoke_cidr` stops being a hand-fitted input — and the **spec-005 registry** —
a Postgres **environment registry** (intent/owner/status) + **provisioning-run audit trail** (dispatch
inputs, GitHub run id/url, outcome). Division of truth holds: ARG is truth for *what's deployed*;
Postgres records *intent*.

**Scope (Clarifications 2026-06-17)**: deliver the **verb layer + registry/audit + run-tracking
subsystem + `pdp` CLI**, runnable **under the owner's context**; **production ACA hosting, public
ingress, and managed identity are deferred to spec 007** (FR-019). Run tracking = `workflow_run`
**webhook** (YARP reverse-proxy ingress → internal handler) **plus polling reconciliation**; the MVP
closes the loop via polling (no public ingress yet), the webhook path built host-ready. Registry lives
in the **existing platform Postgres, new `registry` schema**. Observability = Azure Monitor OTel →
Application Insights, correlated by `env_id` (the App Insights resource ships with the spec-007 host).

This is the platform's first **multi-component .NET service** (after the spec-002 IPAM library and
spec-005 inventory component); it consumes both directly.

## Technical Context

**Language/Version**: .NET 10 (LTS), C# `latest` — pinned via `global.json` and `Directory.Build.props`
(`net10.0`, nullable on, analyzers + warnings-as-errors). No Python.

**Primary Dependencies** (all pinned in `docs/tech-stack.md`; API surfaces verified live via MCP):
- **ASP.NET Core minimal APIs** (10.x) — internal webhook handler + (host-ready) verb endpoints.
- **Wolverine** (`WolverineFx` + `WolverineFx.Postgresql`) — command bus, **durable inbox/outbox**
  (`PersistMessagesWithPostgresql` + `UseEntityFrameworkCoreTransactions`), **scheduled messages**
  (polling reconciler), and the **lifecycle saga** (EF Core saga persistence).
- **EF Core + Npgsql** (10.x) + **`EFCore.NamingConventions`** — the `registry` schema (snake_case);
  native `cidr` ↔ `IPNetwork` reused from spec 002.
- **`Pdp.ControlPlane.Ipam`** (spec 002, in-repo) — `IIpamLedger.AllocateAsync/ReleaseAsync/Query*`
  for Gate-G1 closure.
- **`Pdp.ControlPlane.Inventory`** (spec 005, in-repo) — reused for inventory/env verbs via its
  pluggable `TokenCredential` seam (FR-013).
- **Octokit + `GitHubJwt`** — GitHub App auth (JWT → installation token) → `Actions.Workflows
  .CreateDispatch`. **`Octokit.Webhooks.AspNetCore`** — HMAC validation + typed `workflow_run`.
- **`Yarp.ReverseProxy`** — the public-facing ingress forwarding to the internal handler (Q2).
- **`Microsoft.Extensions.Http.Resilience`** (Polly v8) — outbound GitHub resilience.
- **`FluentValidation`** — verb input validation.
- **System.CommandLine** (2.0 GA) — the `pdp` CLI (`SetAction`/`parseResult.GetValue`).
- **`Azure.Identity`** / **`Azure.ResourceManager.*`** — credential + ARG reads (via spec 005).
- **OpenTelemetry + `Azure.Monitor.OpenTelemetry.AspNetCore`** (`UseAzureMonitor()`) — telemetry.
- **`.NET Aspire`** — local dev orchestration (Postgres + API + ingress, one F5).
- **No prohibited deps**: no MediatR / MassTransit / AutoMapper / Moq / Serilog / FluentAssertions v8+.

**Storage**: the **existing platform Postgres** (the spec-002 IPAM-ledger flexible server). New
**`registry` schema** (`environments`, `provisioning_runs`, saga state) via EF Core migrations,
separate from `ipam` and Wolverine's `wolverine` schema. One DB, one connection — enabling the atomic
allocate-record-dispatch transaction (research §6). **No new Azure resource** is provisioned by this
spec (hosting + App Insights = spec 007).

**Testing**: xUnit + Shouldly + NSubstitute. **`Testcontainers.PostgreSql` + Respawn** for everything
touching the ledger/registry/saga/outbox (real Postgres is **required** — GiST/advisory-lock + outbox
+ saga semantics are untestable in-memory). **`WireMock.Net`** fakes the GitHub API (assert dispatch
inputs; simulate `workflow_run` webhook; simulate **missed** webhook → polling reconcile — SC-006).
**`Microsoft.AspNetCore.Mvc.Testing`** (`WebApplicationFactory`) for the webhook endpoint + end-to-end
verb→dispatch→track. Live proof = the quickstart against `westus3`.

**Target Platform**: a set of .NET 10 projects runnable under the owner's context for the MVP (CLI
in-process; API host for webhook handler + reconciler; YARP ingress), built **host-ready** so spec 007
deploys them to Azure Container Apps (vnet-integrated, internal Postgres, scale-to-zero) with a managed
identity and the single public webhook endpoint — **no verb-logic change**.

**Project Type**: .NET solution additions (libraries + hosts + CLI + tests) wired into `Pdp.sln`,
**plus** one new GitHub Actions dispatch workflow (`fabric-vend.yml`) and `env_id`/`mode` inputs on the
existing env workflows. **No new OpenTofu stack, no new Azure resources.**

**Performance Goals**: SC-012 — dispatch acknowledged/tracking begun **P95 < 5 s** (independent of the
IaC run duration). SC-006 — reconciler sweep **≤ 60 s**, missed-webhook run terminal within **~2 min**.

**Constraints**: Article II (no in-process `tofu`; dispatch only) · Article VI (CIDR only from the
ledger; Gate-G1) · Article VIII (plan→confirm→apply; confirm-before-destroy) · Article IX (one
justified public ingress; least-privilege, **no standing cloud write credential** — GitHub App the
only non-Azure secret) · Article IV (every verb has clean teardown; the spec's footprint = a droppable
schema) · division of truth (FR-016).

**Scale/Scope**: a single-owner platform — a few fabrics, tens of spokes, hundreds of provisioning
runs over time. Concurrency is **single-flight per environment** (FR-022a); cross-environment
operations may proceed in parallel.

## Constitution Check

*GATE: evaluated before Phase 0; re-checked after Phase 1 design. Result: **PASS** — this spec is the
direct embodiment of Articles II, VI, and VIII; the one public endpoint is the Article IX-sanctioned
exception the spec explicitly demands. No deviation requires justification.*

| Article | Gate | Status |
|---|---|---|
| I — Infra is Code | All Azure mutations run as OpenTofu in dispatched CI; the new `fabric-vend.yml` wraps the existing `infra/fabric` stack; registry is app data (EF migrations), not portal mutation. | ✅ |
| II — AI calls verbs / plane split | **Central embodiment**: the control plane is the typed verb layer; it **dispatches** GitHub Actions and **never runs `tofu` in-process** (FR-001/FR-002). | ✅ (central) |
| III — Tagged/tracked, ARG-sourced | "What's deployed?" routes to the spec-005 inventory (ARG); the registry records **intent only**; division of truth enforced in code (FR-016). | ✅ |
| IV — Destroyable | Fabric/spoke destroy with **allocation release** (FR-009); registry → `Destroyed`; the spec's own footprint = the droppable `registry` schema + **no new Azure resources** (hosting deferred). Teardown in acceptance (SC-010). | ✅ |
| V — AVM-first | No new IaC modules; `fabric-vend.yml` dispatches the existing AVM-based stack unchanged. | ✅ N/A |
| VI — No address without allocation | **Central embodiment**: spoke CIDR allocated **only** via `IIpamLedger.AllocateAsync` (Gate-G1 closed); released on destroy; GiST is the non-overlap backstop (FR-008/FR-010). | ✅ (central) |
| VII — Hub owns egress | Unchanged; spoke egress via hub is execution-plane behavior (spec 004); the control plane alters no routing. | ✅ N/A |
| VIII — Plan before apply / confirm destroy | **Central embodiment**: two-phase dispatch (`mode=plan` → confirm → `apply`/`destroy`); destroy confirmation **unbypassable** (FR-006/FR-007). | ✅ (central) |
| IX — Secure & cheap | Private by default; **one** justified public endpoint (YARP webhook ingress → internal handler — the explicit Article IX exception); **no standing cloud write credential** (OIDC in CI); GitHub App the only non-Azure secret; reuses existing Postgres; ACA scale-to-zero later. | ✅ |
| X — Specs before code | specify → clarify → plan → tasks → implement, followed. | ✅ |

**Additional constraints**: .NET 10 pinned ✅ · **no prohibited deps** (Wolverine / NSubstitute /
Shouldly / OTel; explicitly **not** MediatR/MassTransit/AutoMapper/Moq/Serilog/FluentAssertions v8+)
✅ · control vs execution plane preserved — control plane dispatches, OpenTofu runs only in CI via OIDC
✅ · division of truth honored (ARG = deployed; Postgres = intent) ✅ · `.NET` naming mirrors
`Pdp.ControlPlane.Ipam`/`.Inventory` (`Pdp.ControlPlane.Registry` / `.Verbs` / `.Dispatch` / `.Api` /
`.Ingress`, `Pdp.Cli`) ✅ · **Glossary**: `env_id` is **added to `docs/glossary.md`** in this plan
(per request); `provisioning run`, `control plane`, `execution plane` already defined ✅.

**Post-Phase-1 re-check**: the design artifacts introduce no new external endpoints beyond the single
sanctioned webhook ingress, no in-process IaC, and no second source of deployment truth. **Still
PASS** — Complexity Tracking remains empty.

## Project Structure

### Documentation (this feature)

```text
specs/006-control-plane/
├── plan.md              # This file
├── research.md          # Phase 0 — 12 decisions (hosting, saga, plan/confirm, dispatch, tracking, Gate-G1, registry, env_id, CLI, fabric-vend, OTel, testing)
├── data-model.md        # Phase 1 — Environment, ProvisioningRun, EnvironmentSaga, verb/result types, enums, schema notes
├── quickstart.md        # Phase 1 — 8 live validation scenarios + teardown, mapped to SC-001…SC-013
├── contracts/
│   ├── verb-surface.md          # the verb layer (CLI/MCP consume) — fabric/spoke/ipam/inventory/run
│   ├── dispatch-and-tracking.md # IWorkflowDispatcher, IRunTracker, webhook topology, workflow inputs
│   └── cli-surface.md           # the pdp command tree, plan/confirm UX, human + --json
├── checklists/requirements.md
└── tasks.md             # Phase 2 — /speckit-tasks (NOT created here)
```

### Source Code (repository root)

```text
src/Pdp.ControlPlane.Registry/          # NEW EF Core lib — intent + audit (mirrors Pdp.ControlPlane.Ipam)
├── RegistryDbContext.cs                 #   `registry` schema; snake_case; UseEntityFrameworkCoreTransactions target
├── Entities/{Environment,ProvisioningRun}.cs  +  Enums (Kind/Status/RunPhase/RunOutcome/TrackingSource)
├── EnvironmentSaga.cs                   #   Wolverine saga state (lifecycle engine; data-model §3)
├── IEnvironmentRegistry.cs / EnvironmentRegistry.cs   # intent CRUD + status transitions + single-flight guard
├── Migrations/                          #   InitialRegistrySchema
└── README.md

src/Pdp.ControlPlane.Dispatch/          # NEW — execution-plane boundary (Article II)
├── IWorkflowDispatcher.cs / GitHubWorkflowDispatcher.cs   # Octokit + GitHubJwt → CreateDispatch; Polly resilience
├── IRunTracker.cs / RunTracker.cs        #   run-name↔env_id correlation; idempotent terminal record
├── RunReconciler.cs                      #   Wolverine scheduled message (≤60s sweep) — SC-006
└── GitHubAppCredential.cs                #   JWT→installation-token cache (the only non-Azure secret)

src/Pdp.ControlPlane.Verbs/             # NEW — the single verb implementation (CLI + MCP wrap this)
├── IFabricVerbs / ISpokeVerbs / IIpamVerbs / IInventoryVerbs / IRunVerbs   (contracts/verb-surface.md)
├── Handlers/                            #   validate → allocate → record → plan → confirm → apply → track
├── Lifecycle/EnvironmentSagaHandlers.cs #   saga Start/Handle(RunCompleted|Failed|ConfirmationGiven)
├── Validation/                          #   FluentValidation request validators
├── PlanConfirm/                         #   two-phase plan→confirm orchestration (Article VIII)
└── ServiceCollectionExtensions.cs       #   AddControlPlaneVerbs(...) — DI wiring (Wolverine, EF, Ipam, Inventory)

src/Pdp.ControlPlane.Api/               # NEW ASP.NET Core host (internal) — built host-ready (spec 007 deploys)
├── Program.cs                           #   Wolverine + EF + outbox; UseAzureMonitor(); reconciler schedule
├── Webhooks/GitHubWebhookHandler.cs     #   Octokit.Webhooks.AspNetCore — HMAC + typed workflow_run → inbox
└── Endpoints/                           #   (host-ready) verb endpoints for the future MCP/remote CLI

src/Pdp.ControlPlane.Ingress/           # NEW YARP reverse proxy — the ONE public surface (Article IX)
├── Program.cs                           #   forward POST /webhooks/github → internal Api; no business logic
└── appsettings.json                     #   YARP route/cluster

src/Pdp.Cli/                            # NEW — the `pdp` CLI (System.CommandLine 2.0 GA)
├── Program.cs                           #   RootCommand + verb subcommands; global --json/--verbose
├── Commands/{Fabric,Spoke,Ipam,Inventory,Env,Run}Command.cs
├── Rendering/{HumanRenderer,JsonRenderer}.cs   # one typed result → table | System.Text.Json (SC-008)
└── README.md                            #   "this is the pdp CLI (spec 006); MCP is spec 007"

src/Pdp.AppHost/                        # NEW .NET Aspire — local dev (Postgres + Api + Ingress, one F5)
└── AppHost.cs

tests/Pdp.ControlPlane.Registry.Tests/  # NEW — Testcontainers Postgres + Respawn
├── status transitions, unique natural key, single-flight guard, schema migration/teardown
tests/Pdp.ControlPlane.Verbs.Tests/     # NEW — Testcontainers Postgres + WireMock.Net + NSubstitute
├── Gate-G1 allocate-on-create, release-on-destroy, no-leak-on-failure, idempotent re-create, plan/confirm, single-flight
tests/Pdp.ControlPlane.Dispatch.Tests/  # NEW — WireMock.Net fake GitHub
├── dispatch inputs (env_id/mode/spoke_cidr), run-name correlation, webhook vs missed-webhook→reconcile (SC-006), idempotent record
tests/Pdp.ControlPlane.Api.Tests/       # NEW — WebApplicationFactory
├── webhook HMAC validation + typed workflow_run; end-to-end verb→dispatch→track
tests/Pdp.Cli.Tests/                    # NEW — NSubstitute + Shouldly
├── command parsing, plan/confirm UX, human vs --json rendering, exit codes

.github/workflows/fabric-vend.yml       # NEW — workflow_dispatch create over infra/fabric (FR-012a)
.github/workflows/{spoke-vend,spoke-destroy,fabric-destroy}.yml   # EDIT — add env_id + mode inputs + run-name
Pdp.sln                                 # EDIT — add the new src/ + tests/ projects
docs/glossary.md                        # EDIT — add the `env_id` term (planning request)
```

**Structure Decision**: Split the control plane along its bounded concerns, each a small library
mirroring the established `Pdp.ControlPlane.*` shape: **Registry** (persistence/intent/audit, like
`Ipam`), **Dispatch** (the execution-plane seam — the only GitHub-touching code, isolated for
WireMock testing), **Verbs** (the single front-end-agnostic implementation behind CLI + MCP), and the
**Api** / **Ingress** hosts (built now, deployed by spec 007). The **`IWorkflowDispatcher` /
`IRunTracker` seams** mirror spec 005's `IResourceGraphReader` decision: they keep the verb/lifecycle
logic a pure, fully testable core while isolating the version-sensitive Octokit/webhook surface behind
adapters the WireMock tests exercise. The verb layer reuses `Pdp.ControlPlane.Ipam` (Gate-G1) and
`Pdp.ControlPlane.Inventory` (FR-013) **directly** — no reimplementation. There is **no new Azure
resource and no new OpenTofu module**; the single infra addition is a dispatch workflow wrapping an
existing stack.

## Complexity Tracking

> No constitution violations. The single public endpoint is the Article IX-sanctioned exception the
> spec explicitly demands (the webhook ingress), not a deviation. The table is intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| _(none)_ | — | — |
