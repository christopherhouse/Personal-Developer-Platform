# Phase 0 — Research: Action Layer (Control Plane)

Resolves the technical unknowns behind spec 006 before design. Every decision is grounded in the
constitution, the spec's clarifications, `docs/tech-stack.md` (which already pins the action-layer
libraries), the existing `Pdp.ControlPlane.Ipam` surface, and a live check of the version-sensitive
APIs via the MCP doc tools (Wolverine, Octokit, System.CommandLine, Azure Monitor OTel).

Format per decision: **Decision · Rationale · Alternatives rejected.**

---

## 1. Where the verb layer runs for the MVP (Clarifications Q1 — hosting deferred)

**Decision.** The **verb layer is a class library** (`Pdp.ControlPlane.Verbs`) — the single
implementation behind the CLI now and the spec-007 MCP later. For the MVP it runs **in-process inside
the `pdp` CLI** and inside an ASP.NET Core host (`Pdp.ControlPlane.Api`) used for the
webhook-handler + reconciler. Both run **under the owner's context** (which can reach the private
platform Postgres and GitHub). This spec provisions **no ACA, no public ingress, no managed
identity** — those land with the spec-007 host (FR-019).

**Rationale.** Mirrors spec 005's "component + thin surface" pattern and the constitution's plane
split: the verb library is reusable and front-end-agnostic. Running it in-process in the CLI is the
cheapest way to demonstrate the full spine (validate → allocate → record → dispatch → track) with the
private-ledger reachability that CI lacks — the exact reason Gate-G1 was deferred to the control
plane.

**Alternatives rejected.** (a) Stand up ACA now — rejected by Q1 (hosting is spec 007) and Article IX
(cheap by default). (b) CLI-talks-to-hosted-API only — rejected: there is no host yet, and the verb
*library* must exist regardless so both front ends wrap one implementation (FR-001).

**Tracking responsibility (resolves analysis A1).** The **durable, continuously-running** reconciler
that closes the loop lives in the **Api host** (long-lived; spec-007-hosted). A foreground `pdp`
command dispatches then **polls for its own result** to terminal (or returns immediately with
`--no-wait`, printing the `env_id`/run URL); it does not rely on a running host to *dispatch*. This
avoids running a long-lived reconciler inside a short-lived CLI process while keeping the MVP usable
under the owner's context (contracts/cli-surface.md §4).

---

## 2. Lifecycle as a Wolverine saga keyed by `env_id` (Clarifications Q1/Q2; FR-014, FR-022a)

**Decision.** Model each environment's lifecycle as a **Wolverine stateful saga** persisted in the
control-plane Postgres via EF Core, keyed by the surrogate **`env_id`**. States:
`Requested → Provisioning → Active | Failed` and `Destroying → Destroyed | Failed`. The saga's
existence + non-terminal status is the **single-flight guard** (FR-022a): a mutating command for an
environment whose saga is non-terminal is rejected ("operation already in progress"). The saga
`Start` cascades a `DispatchWorkflow` message; a `RunCompleted` event (from webhook or reconciler)
advances or fails it; `MarkCompleted()` retires terminal sagas.

**Rationale.** Verified live: Wolverine sagas (`class X : Saga`, `Start(...)` returning cascading
messages, `Handle(event)` transitions, `MarkCompleted()`) with **EF Core saga persistence**
(`UseEntityFrameworkCoreTransactions`) are the documented pattern for exactly this long-running,
durable, event-driven lifecycle. Sagas give us the status machine, durability across restarts, and
the concurrency guard in one construct, on the Postgres we already run.

**Alternatives rejected.** (a) Hand-rolled status column + polling loop — reimplements durable
messaging Wolverine already provides (a prohibited-reinvention smell). (b) A separate state machine
library — unnecessary dependency; Wolverine is already pinned (`docs/tech-stack.md`). (c) MediatR
request/response — **prohibited dependency**; Wolverine is the command bus.

---

## 3. Plan-before-apply / confirm-before-destroy as two-phase dispatch (Article VIII; FR-006/FR-007)

**Decision.** Realize Article VIII as **two-phase dispatch**. Every mutating verb first dispatches the
workflow in **`plan` mode** (`tofu plan`, no apply); the control plane tracks that run, captures its
plan summary/outcome, and **surfaces it**. Only after confirmation does it dispatch the **`apply`**
(or **`destroy`**) phase. For **create**, confirmation MAY be implicit via a `--yes` flag after the
plan is shown; for **destroy**, explicit confirmation is **mandatory and unbypassable** (restating the
target). Dispatched workflows therefore take a **`mode` input** (`plan` | `apply` | `destroy`).

**Plan-output retrieval (resolves analysis U1).** The `mode=plan` run writes `tofu plan` to the GitHub
**Step Summary** *and* uploads `plan.txt`/`plan.json` as a **run artifact**; once the plan run is
terminal, `IRunTracker` **downloads the artifact via the GitHub Actions API** (installation-token
auth) into `ProvisioningRun.PlanSummary`, which the verb's `PlanResult` surfaces for confirmation. A
missing/oversized artifact is never treated as approval — the control plane surfaces the run URL and
requires review in GitHub (contracts/dispatch-and-tracking.md §6).

**Rationale.** This maps Article VIII precisely onto the execution plane (the only place `tofu`
runs — Article II) and reuses the same dispatch+tracking machinery for both phases. The existing
`iac-plan.yml` / `iac-apply.yml` reusable workflows already separate plan from apply; the
env-operation workflows (`spoke-vend`, the new `fabric-vend`, `*-destroy`) gain a `mode` input so the
control plane owns the plan→confirm→apply sequence rather than burying it in a single run.

**Alternatives rejected.** (a) Single run that plans-then-applies with a GitHub Environments manual
approval gate — moves the confirmation out of the control plane/CLI where the owner actually is, and
the spec requires confirmation "from chat/CLI." (b) Lightweight "intent summary" only (no real `tofu
plan`) — weaker than Article VIII intends; the real plan is cheap to dispatch and far more
trustworthy. (Intent summary is still shown alongside the plan for readability.)

---

## 4. GitHub dispatch + `env_id` correlation (FR-002, FR-003; Octokit + GitHubJwt)

**Decision.** Dispatch via **Octokit** authenticated as the **`pdp-orchestrator` GitHub App**:
`GitHubJwt.GitHubJwtFactory` (private key → 10-min JWT) → `appClient.GitHubApps
.CreateInstallationToken(installationId)` (1-hour installation token) → an installation-scoped
`GitHubClient` calling **`Actions.Workflows.CreateDispatch(owner, repo, workflowFile, new
CreateWorkflowDispatch(gitRef){ Inputs = {... , ["env_id"] = envId, ["mode"] = mode } })`**. Outbound
GitHub calls wrap **`Microsoft.Extensions.Http.Resilience`** (Polly v8) retry/timeout.

**Correlation.** `workflow_dispatch` returns no run id, and the `workflow_run` webhook payload does
not echo inputs. We correlate by making the dispatched workflow set a unique **`run-name`** embedding
`env_id` and `mode` (`run-name: pdp ${{ inputs.mode }} ${{ inputs.env_id }}`); the control plane then
maps a `workflow_run` (webhook **or** reconciler list) back to its `env_id` by parsing `run-name`
(`display_title`). A `provisioning_runs` row is written at dispatch time (status `dispatched`) and
updated when the matching run resolves.

**Rationale.** Verified live: the GitHubJwt → installation-token → installation-client flow is the
documented Octokit GitHub-App pattern; `docs/tech-stack.md` pins `Octokit + GitHubJwt`. The
`run-name`-carries-`env_id` technique is the standard, robust way to correlate dispatched runs back to
a request given GitHub's API gaps. The GitHub App installation token is short-lived (no PAT), keeping
the **GitHub App credential the only non-Azure secret** (FR-020).

**Alternatives rejected.** (a) Stored PAT — violates the "no stored secrets beyond the GitHub App"
constraint. (b) `repository_dispatch` — less ergonomic for env-scoped inputs and loses the Actions UI
"Run workflow" affordance. (c) Correlating by `created>=t & event=workflow_dispatch` time-window
queries — racy under concurrent dispatches; `run-name` is deterministic.

---

## 5. Run tracking: webhook (YARP ingress → internal handler) + polling reconcile (Clarifications Q2; FR-004/FR-005, SC-006)

**Decision.** Two complementary signals, both correlated by `env_id`:

1. **Webhook (primary, host-ready).** An ASP.NET Core endpoint built with
   **`Octokit.Webhooks.AspNetCore`** (HMAC-SHA256 signature validation + typed `workflow_run`
   payloads) lives in the **internal** `Pdp.ControlPlane.Api` (no direct public exposure). A separate
   **YARP reverse-proxy** app (`Pdp.ControlPlane.Ingress`) is the only public-facing surface and
   forwards `POST /webhooks/github` to the internal handler. Webhook processing is enqueued through
   **Wolverine's durable inbox/outbox** so it is **idempotent** against duplicate/late deliveries
   (dedupe by GitHub delivery id + run id).
2. **Polling reconciler (guarantees completion).** A **Wolverine scheduled message**
   (`ScheduleAsync`/`DelayedFor`, self-rescheduling at **≤60 s**) lists environments with a
   non-terminal in-flight run and queries the GitHub Actions API for each run's status, driving it to
   terminal and emitting `RunCompleted`. Bound: a missed-webhook run reaches recorded terminal status
   within **~2 min** (SC-006).

For the **MVP** (no public ingress yet — Q1), the **reconciler alone closes the loop**; the
webhook ingress→handler path is built and tested so spec 007 activates it by hosting, with no
verb-logic change.

**Rationale.** Verified live: `Octokit.Webhooks.AspNetCore` (pinned in `docs/tech-stack.md`) gives
signature validation + typed `workflow_run` for free; Wolverine scheduled messages + durable
local queues (verified) are the idiomatic way to run a resilient recurring reconciler and an
idempotent inbox on Postgres. The user's explicit topology (YARP ingress container → private handler
container) is honored and is the Article IX-sanctioned single public endpoint, kept thin and
forwarding-only (defense in depth).

**Alternatives rejected.** (a) Webhook-only — a single missed delivery strands a run "in flight"
(SC-006 fails). (b) Polling-only — works for the MVP but loses low-latency completion once hosted;
both are cheap to keep. (c) Hosted `BackgroundService` timer instead of Wolverine scheduled messages
— loses durability/retry/back-pressure Wolverine already provides on the same Postgres.

---

## 6. Gate-G1 closure: live by-size IPAM allocation at vend (Article VI; FR-008/FR-009/FR-011)

**Decision.** The spoke-create verb allocates the block by calling the **existing spec-002 allocator**
— `IIpamLedger.AllocateAsync(region, spokeName, prefixLength)` — which reserves the lowest free
aligned block of the requested size from the region `/16` and returns `Allocation.Network`. That CIDR
becomes the `spoke_cidr` **dispatch input**; `spoke_cidr` is **removed** as a typed user input.
Spoke-destroy calls **`ReleaseAsync(region, spokeName)`** after the destroy run succeeds.
**Atomicity:** the allocation write, the environment-registry insert, and the dispatch enqueue happen
in **one EF Core transaction with Wolverine's durable outbox** — the dispatch message is only sent if
the transaction commits; if the run later fails, the saga **compensates** by releasing the allocation
(no leaked row, FR-011/FR-025).

**Rationale.** `AllocateAsync` is idempotent on `name` (re-vend converges — FR-022) and the GiST
exclusion constraint is the absolute non-overlap backstop (Article VI), so the control plane never
invents address space. The allocator's own doc comment states it is "consumed by the spec-006 verbs."
The outbox guarantees allocate-and-dispatch are all-or-nothing.

**Alternatives rejected.** (a) Keep `spoke_cidr` as a typed input — that is exactly the Gate-G1
deferral this spec closes. (b) Allocate inside the workflow (CI) — impossible: CI cannot reach the
private ledger (the original Gate-G1 blocker). (c) Allocate without the outbox — risks a committed
allocation with no dispatch (leak) on a crash between write and send.

---

## 7. Registry + audit persistence (Clarifications Q3; FR-014–FR-017)

**Decision.** A new **`Pdp.ControlPlane.Registry`** EF Core library (mirroring
`Pdp.ControlPlane.Ipam`) on the **existing platform Postgres**, in a **new schema `registry`**,
distinct from the IPAM `ipam` schema and from Wolverine's `wolverine` schema. Tables (snake_case via
`EFCore.NamingConventions`): **`environments`** (intent + lifecycle) and **`provisioning_runs`**
(audit). EF Core migrations create the schema; **division of truth** is enforced in code — "what's
deployed?" routes to the spec-005 inventory (ARG), never to `environments` (FR-016).

**Rationale.** Q3 chose the existing server + new schema (cheapest, co-located with the ledger the
verbs already write, one DB to operate). Separate schemas keep IPAM, registry, and Wolverine envelopes
cleanly isolated while sharing one connection/transaction (enabling the §6 atomic outbox across ledger
+ registry).

**Alternatives rejected.** (b) Separate database/server — rejected by Q3 (cost/teardown). (a) Folding
registry tables into the `ipam` schema — muddies two distinct bounded contexts and complicates
teardown (Article IV wants the registry schema droppable on its own).

---

## 8. `env_id` shape (Clarifications Q1-data; FR-014) + glossary

**Decision.** `env_id` is a **generated stable surrogate** — a **UUIDv7** (time-ordered, index- and
correlation-friendly) — serving as the `environments` primary key and the workflow correlation key.
The registry additionally enforces a **unique natural key `(kind, subscription, name)`** for
idempotent convergence (FR-022). **`env_id` is added to `docs/glossary.md`** (per the planning
request) so the term is canonical platform-wide.

**Rationale.** A surrogate is immutable and collision-free across the dispatch/webhook round-trip;
UUIDv7's time-ordering avoids index fragmentation versus random UUIDv4. The natural key is what makes
re-create converge (matching spec 004's `(subscription, spoke-name)` identity). The constitution
requires new terms be glossary-defined before use.

**Alternatives rejected.** (a) Natural key as the correlation id — brittle if a name is ever
re-cased/renamed and awkward in a `run-name`. (b) Random UUIDv4 — fine but worse index locality;
UUIDv7 is the modern default.

---

## 9. The `pdp` CLI (FR-018; System.CommandLine 2.0 GA)

**Decision.** `Pdp.Cli` (`pdp`) built on **System.CommandLine 2.0 GA**: a `RootCommand` with verb
subcommands (`fabric`, `spoke`, `ipam`, `inventory`/`env`, `run`), `Option<T>`/`Argument<T>` inputs,
and `SetAction(async (parseResult, ct) => …)` handlers that call the in-process verb layer. A global
**`--json`** option switches every command from human-readable tables to `System.Text.Json` of the
same typed result (FR-018 / SC-008). FluentValidation validates inputs before the verb runs.

**Rationale.** Verified live the GA surface (`new Option<T>("--x"){ Description, DefaultValueFactory }`,
`command.Options.Add` / `Subcommands.Add`, `parseResult.GetValue(opt)`, async `SetAction`,
`rootCommand.Parse(args).InvokeAsync()`) — materially different from the old `SetHandler` beta API, so
pinning to the GA shape matters. `docs/tech-stack.md` pins System.CommandLine for the CLI.

**Alternatives rejected.** (a) Ookii.CommandLine / Spectre.Console.Cli — not the pinned choice; no
reason to deviate. (b) Hand-rolled arg parsing — loses help/completion/validation.

---

## 10. New `fabric create` dispatch workflow (Clarifications Q3-fabric; FR-012a)

**Decision.** Add **`.github/workflows/fabric-vend.yml`** — a `workflow_dispatch` create over the
existing `infra/fabric` stack, mirroring `spoke-vend.yml` / `fabric-destroy.yml`, taking `env_id` +
`mode` (`plan`|`apply`) inputs and setting the `env_id`-bearing `run-name`. Add the same `env_id` +
`mode` inputs (and `run-name`) to `spoke-vend.yml` and the `*-destroy.yml` workflows so all
fabric/spoke verbs dispatch uniformly. No new Azure infrastructure — these wrap existing stacks on the
spec-001 OIDC rails.

**Rationale.** The `westus3` fabric was first deployed via GitOps `iac-apply`-on-merge, leaving no
dispatchable create path; without it the verb layer cannot honor `fabric create` (FR-012a). The
addition is a thin dispatch wrapper, AVM/stack unchanged (Article V N/A).

**Alternatives rejected.** Leaving `fabric create` GitOps-only (Q3 chose to add it); reusing
`iac-apply` directly (loses the env-scoped `mode`/`env_id` inputs and uniform correlation).

---

## 11. Observability: Azure Monitor OTel → App Insights (Clarifications Q5; FR-O1, SC-013)

**Decision.** Instrument with **`builder.Services.AddOpenTelemetry().UseAzureMonitor()`**
(`Azure.Monitor.OpenTelemetry.AspNetCore`), a custom `ActivitySource`/`Meter` registered via
`AddSource`/`AddMeter`, and **`env_id` stamped on every span/log** (verb invocation → dispatch →
run-state transition). Connection string via **`APPLICATIONINSIGHTS_CONNECTION_STRING`** env var
(managed-identity `Credential` later on ACA). The **App Insights resource ships with the spec-007
host**; for the MVP, telemetry exports to a **dev sink** (a dev App Insights connection string, or the
console/OTLP exporter) — so this spec adds **no new Azure resource** (FR-O1 / SC-013).

**Rationale.** Verified live: `UseAzureMonitor()` reads `APPLICATIONINSIGHTS_CONNECTION_STRING`, wires
traces/logs/metrics, and supports AAD `Credential`; `AddSource`/`AddMeter` surface custom spans.
`env_id`-correlated traces make a stuck/failed run debuggable end to end (the whole point for an
AI-operated dispatcher).

**Alternatives rejected.** (a) Serilog — **prohibited**; built-in `ILogger` + OTel suffices. (b)
Provisioning App Insights in this spec — conflicts with Q1's "no new Azure resources / hosting in
007." (c) Azure-side diagnostic routing / Policy / cost — that is spec 010, not control-plane app
telemetry.

---

## 12. Testing strategy (Article: testing discipline; `docs/tech-stack.md`)

**Decision.**
- **Real Postgres via `Testcontainers.PostgreSql` + Respawn** for everything that touches the
  ledger/registry/saga: allocation atomicity, allocate-release lifecycle, registry status
  transitions, the unique natural key, single-flight rejection, and Wolverine durable outbox/inbox.
  (The constitution **requires** real Postgres for IPAM — the GiST/advisory-lock behavior — and the
  outbox + saga semantics are equally untestable in-memory.)
- **`WireMock.Net`** as the fake GitHub API: assert exact dispatch inputs (`env_id`, `mode`,
  `spoke_cidr`, target), simulate a `workflow_run` webhook delivery, and simulate a **missed** webhook
  to prove the polling reconciler still drives the run terminal (SC-006).
- **`WebApplicationFactory`** (`Microsoft.AspNetCore.Mvc.Testing`) for the webhook endpoint
  (HMAC validation, typed `workflow_run`) and end-to-end verb→dispatch→track.
- **NSubstitute + Shouldly** for pure units (CLI parsing/rendering, correlation parsing, validators);
  the CLI's `--json` rendering is snapshot-asserted (structured-output proof, SC-008).

**Rationale.** Matches the pinned test stack and the constitution's "integration tests against real
dependencies where behavior depends on them." GitHub and Azure are faked at their seams so no live
cloud is needed for unit/integration runs; live proof is the quickstart against `westus3`.

**Alternatives rejected.** In-memory Postgres/SQLite — cannot reproduce GiST, advisory locks, or
Wolverine Postgres durability (false confidence). Moq — **prohibited** (NSubstitute is the choice).

---

## Resolved unknowns

| # | Unknown | Resolution |
|---|---|---|
| 1 | Where the verb layer runs (MVP) | In-process library in CLI + API host, owner context; no ACA (§1) |
| 2 | Lifecycle/state + concurrency | Wolverine saga keyed by `env_id`; non-terminal = single-flight guard (§2) |
| 3 | Plan/confirm mapping | Two-phase dispatch (`mode=plan` → confirm → `apply`/`destroy`) (§3) |
| 4 | Dispatch + correlation | Octokit GitHub App; `env_id` in `run-name` (§4) |
| 5 | Tracking resilience | Octokit.Webhooks via YARP ingress + Wolverine-scheduled polling reconciler (§5) |
| 6 | Gate-G1 allocation | `IIpamLedger.AllocateAsync` by size, released on destroy, atomic via outbox (§6) |
| 7 | Registry storage | Existing Postgres, new `registry` schema, EF Core (§7) |
| 8 | `env_id` shape | UUIDv7 surrogate + unique `(kind, subscription, name)`; glossary updated (§8) |
| 9 | CLI | System.CommandLine 2.0 GA; global `--json` (§9) |
| 10 | fabric create path | New `fabric-vend.yml` + `mode`/`env_id` inputs on env workflows (§10) |
| 11 | Observability | Azure Monitor OTel → App Insights, `env_id`-correlated; resource in spec 007 (§11) |
| 12 | Testing | Testcontainers Postgres + Respawn; WireMock.Net for GitHub; WAF for endpoints (§12) |

No `NEEDS CLARIFICATION` markers remain. Proceed to Phase 1.
