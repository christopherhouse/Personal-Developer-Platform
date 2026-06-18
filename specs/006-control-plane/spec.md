# Feature Specification: Action Layer — Control Plane

**Feature Branch**: `006-control-plane`

**Created**: 2026-06-17

**Status**: Draft

**Input**: User description: "Build spec 006 — the action layer (control plane): the .NET 10 service that turns owner intent into validated, dispatched, and tracked infrastructure operations. Wrap specs 2–5 behind typed verbs and expose them through the pdp CLI so 'deploy me a spoke' becomes one command. The control plane dispatches; it never executes IaC (Article II). Plan before apply; explicit confirmation before destroy (Article VIII). Close the deferrals: Gate-G1 (live by-size IPAM allocation at vend) and the spec-005 Postgres registry of intent + provisioning-run audit trail."

## Overview

The action layer is PDP's **control plane**: the .NET service that turns owner intent into
**validated, dispatched, and tracked** infrastructure operations. It wraps the capabilities built by
specs 002–005 behind **typed verbs** and exposes them through the **`pdp` CLI**, so the critical-path
promise — *"deploy me a spoke"* — becomes a single command instead of a hand-fitted CIDR plus a
hand-run GitHub workflow.

The plane split is non-negotiable (constitution Article II): **the control plane dispatches; it never
executes IaC.** Every mutating verb validates the request, allocates address space from the IPAM
ledger where needed, records intent in Postgres, then **dispatches a GitHub Actions workflow**
(GitHub App + `workflow_dispatch`) that runs OpenTofu authenticated to Azure via OIDC — and tracks
that run to completion, correlated by `env_id`. OpenTofu never runs in-process. Every mutation shows
its **plan before apply**, and every destroy requires **explicit human confirmation** (Article VIII).

This spec closes the two deferrals earlier specs deliberately left for the control plane:

- **Gate-G1 (spec 004):** spoke address space is now **allocated live by size from the IPAM ledger at
  vend time** — the control plane can reach the private Postgres ledger that CI cannot — so
  `spoke_cidr` stops being a hand-fitted typed input, and the allocation row is **released on
  destroy**.
- **Spec 005:** stand up the **Postgres registry** recording intent (kind / owner / status) and the
  **provisioning-run audit trail** (dispatch inputs, GitHub run id/url, outcome) — the records spec
  005 deliberately did not read. **Division of truth holds:** Azure Resource Graph is the truth for
  *what is deployed*; Postgres records *intent*.

The **verb layer is the single implementation** behind both the `pdp` CLI now and the spec-007 MCP
server later — neither front-end reimplements a verb.

## Clarifications

### Session 2026-06-17

- Q: Does spec 006 provision the Azure-hosted control plane, or deliver the verb layer + registry +
  CLI with hosting deferred? → A: **Logic now, hosting in spec 007.** Spec 006 delivers the typed
  verbs, the Postgres registry + audit trail, the plan/confirm flow, the run-tracking subsystem, and
  the `pdp` CLI, runnable under the owner's context (which can reach the private platform Postgres and
  GitHub). The **ACA-hosted service, the public ingress endpoint, and the control-plane managed
  identity are deferred to the spec-007 MCP host**; the components are built so 007 hosts them with no
  verb-logic change. Matches spec-005's "component + thin surface" pattern and the backlog (ACA
  hosting is spec 7).
- Q: How does the control plane learn a dispatched workflow's outcome (correlated by `env_id`)? → A:
  **`workflow_run` webhook plus polling reconciliation.** The inbound webhook is terminated by a
  public-facing **reverse-proxy (YARP) ingress** that forwards to a **separate internal
  webhook-handler** with no direct public exposure (defense in depth), and a **polling reconciler**
  sweeps for terminal status so a missed webhook delivery never leaves a run stuck "in flight."
  Because production ingress hosting is deferred (Q1), the MVP closes the tracking loop via **polling**
  (no public endpoint required yet); the webhook ingress→handler path is built and tested so it
  activates when spec 007 hosts it.
- Q: Where does the environment registry + provisioning-run audit trail live? → A: **The existing
  platform Postgres** (the IPAM-ledger flexible server), in a **new dedicated schema**, separate from
  the IPAM ledger tables, in the same database.
- Q: What is `env_id`'s shape and uniqueness rule? → A: **Both a surrogate and a natural key.**
  `env_id` is a **generated stable surrogate** (immutable; the correlation key passed to and echoed
  back from dispatched workflows, and the registry primary key), and the registry **additionally**
  enforces a **unique natural key `(kind, subscription, name)`** that drives idempotent
  convergence (FR-022) — so a re-create resolves to the existing environment rather than minting a new
  surrogate.
- Q: What happens when a mutating verb targets an environment that already has an in-flight run? → A:
  **Reject (single-flight per environment).** A mutating verb against an environment whose status is
  non-terminal (a run is in flight) is rejected with a clear "operation already in progress" error;
  the owner retries once it settles. **Read** verbs (inventory, query, run status) are always allowed.
- Q: Does spec 006 build the missing `fabric create` dispatch path, or wrap only what is dispatchable
  today? → A: **Add the `fabric create` dispatch workflow.** Spec 006 adds a `workflow_dispatch`
  create workflow over the existing `infra/fabric` stack (mirroring `spoke-vend` / `fabric-destroy`),
  so **all** fabric/spoke verbs dispatch uniformly (Article II) — the fabric stops being a
  GitOps-only special case.
- Q: What is the reconcile time bound for a missed-webhook run to reach terminal status? → A:
  **≤60s sweep / ~2 min settle.** The polling reconciler runs at least every 60 seconds, and a run
  whose webhook was missed reaches its recorded terminal status within ~2 minutes of the run actually
  finishing.
- Q: What observability does spec 006 own, and built on what? → A: **In-scope minimal, built on the
  Azure Monitor OpenTelemetry Distro → Application Insights.** The control plane emits structured
  **logs + traces (and basic metrics) correlated by `env_id`** across verb invocations and
  run-state transitions, instrumented via the Azure Monitor OTel distro (Application Insights). The
  **App Insights resource** is provisioned with the deferred **hosting (spec 007)** — keeping spec 006
  free of new Azure resources (Q1); 006 builds the instrumentation and exports to a dev/local sink for
  the MVP. **Azure-side** diagnostic routing, Policy enforcement, and cost visibility remain **spec
  010**.

## User Scenarios & Testing *(mandatory)*

### User Story 1 - One-command spoke vend, validated → allocated → dispatched → tracked (Priority: P1) 🎯 MVP

The owner runs one command — e.g. `pdp spoke create --env demo --region westus3 --subscription <id>
--name app3 --size /24`. The control plane validates the request, **allocates the spoke's address
block live by size from the IPAM ledger** (closing Gate-G1), records the intent in the registry,
**dispatches the spoke-vend GitHub Actions workflow** with the allocated CIDR and an `env_id`
correlation input, tracks the run to completion, and records the outcome in the audit trail. The
owner never hand-fits a CIDR and never hand-runs the workflow; the new spoke appears in inventory.

**Why this priority**: This is the critical-path payoff of specs 1–6 — *"deploy me a spoke"* as one
command — and it exercises the whole control-plane spine (validate → allocate → record → dispatch →
track → record outcome) end to end. On its own it delivers a usable, demoable capability.

**Independent Test**: Run `pdp spoke create` against the live `westus3` fabric and a writable target
subscription; confirm the control plane allocated a ledger row by size, dispatched the workflow with
the allocated CIDR + `env_id`, tracked it to success, recorded the run, and that the spoke is
discoverable in inventory — without any other story.

**Acceptance Scenarios**:

1. **Given** a registered region with a deployed fabric and a writable target subscription, **When**
   the owner runs `spoke create` with a requested size, **Then** the control plane allocates a
   non-overlapping block of that size from the region's IPAM ledger, records intent (status
   `provisioning`), dispatches the spoke-vend workflow with the allocated CIDR and an `env_id`, and
   reports the dispatched run.
2. **Given** a dispatched spoke-vend run, **When** it completes successfully, **Then** the control
   plane records the terminal outcome (run id/url) in the audit trail, marks the environment `active`,
   and the spoke is discoverable in inventory by its `pdp-*` tags.
3. **Given** the owner never supplies a CIDR, **When** `spoke create` runs, **Then** the address
   block comes **solely** from the IPAM ledger (Article VI) — `spoke_cidr` is no longer a typed input
   (Gate-G1 closed).
4. **Given** an invalid request (unregistered region, no deployed fabric, non-writable subscription,
   or exhausted address space), **When** `spoke create` runs, **Then** it fails fast **before**
   recording intent or dispatching, leaking no allocation and creating no orphaned registry row.

---

### User Story 2 - Plan before apply, confirm before destroy (Priority: P1)

Every mutating verb surfaces a **plan** of the intended change before anything is applied, and every
**destructive** verb requires **explicit human confirmation** before it dispatches. The owner runs
`pdp spoke destroy --env demo --name app3`, sees exactly what will be removed, confirms, and only
then does the control plane dispatch the destroy workflow and — on success — **release the IPAM
allocation row** so the block is reusable.

**Why this priority**: Plan-before-apply and confirm-before-destroy are constitutional guardrails
(Article VIII) and the difference between a safe AI-operated platform and a dangerous one. They apply
to the create path (US1) and are the entire point of the destroy path; together with US1 they form
the trustworthy core.

**Independent Test**: Run a create verb and confirm it surfaces a plan before dispatching; run
`spoke destroy` and confirm it refuses to proceed without an explicit confirmation step, then after
confirmation dispatches the destroy and releases the ledger allocation — verifiable by re-vending
into the freed block.

**Acceptance Scenarios**:

1. **Given** any mutating verb, **When** it is invoked, **Then** it surfaces a plan of the intended
   change before any apply is dispatched (Article VIII).
2. **Given** a destroy verb, **When** it is invoked, **Then** it does **not** dispatch until an
   explicit, unambiguous confirmation is given (e.g., restating the target); the confirmation step
   cannot be silently bypassed — including from chat/CLI.
3. **Given** a confirmed `spoke destroy`, **When** the destroy run succeeds, **Then** the control
   plane releases the spoke's IPAM allocation row (block reusable), marks the environment `destroyed`,
   and records the run outcome — completing the teardown spec 004 left to the control plane.
4. **Given** a destroy run that fails or partially completes, **When** it terminates, **Then** the
   control plane does **not** release the allocation, leaves the environment in a `failed`/`destroying`
   status with the run id/url recorded, and the operation is safely re-runnable.

---

### User Story 3 - The full verb surface across specs 2–5 behind one CLI (Priority: P2)

The owner drives the whole platform through one CLI: `pdp fabric create|destroy`, `pdp spoke
create|destroy`, `pdp ipam allocate|release|query`, and `pdp inventory` / `pdp env list|show`. Every
verb produces both human-readable output and structured `--json`, and every verb is the **same
implementation** the spec-007 MCP server will wrap — no per-front-end reimplementation.

**Why this priority**: The action layer's reason to exist is to wrap specs 2–5 behind typed verbs
consumable identically by CLI and MCP. It builds on the create/destroy spine (US1/US2) and is how the
owner actually operates the platform day-to-day, but it is breadth on top of the proven core.

**Independent Test**: Invoke each verb from the `pdp` CLI in both default and `--json` modes; confirm
each returns correct results, the `--json` is consumable without scraping human text, and inventory/
env queries reuse the spec-005 component (no duplicate inventory logic).

**Acceptance Scenarios**:

1. **Given** the `pdp` CLI, **When** the owner runs any wrapped verb (`fabric`, `spoke`, `ipam`,
   `inventory`/`env`), **Then** it executes through the typed verb layer and renders both
   human-readable and `--json` output carrying the same data.
2. **Given** an inventory or environment query, **When** it runs, **Then** it reuses the existing
   `Pdp.ControlPlane.Inventory` component (spec 005) with the control plane's credential injected into
   its pluggable credential seam — no inventory logic is duplicated.
3. **Given** the verb layer, **When** the spec-007 MCP server is later built, **Then** it can wrap the
   same verbs with no reimplementation (one verb implementation behind both front-ends).

---

### User Story 4 - Intent registry + provisioning-run audit trail (Priority: P2)

The control plane records, in Postgres, the **intent** for every environment (kind, target
subscription, region, owner, lifecycle status) and an **audit trail** of every provisioning run
(dispatch inputs, GitHub run id/url, correlating `env_id`, outcome, timestamps). The owner can answer
*"what did I ask for, and what happened?"* from the registry — while *"what's deployed?"* still comes
from inventory (Resource Graph). Division of truth is preserved.

**Why this priority**: The registry and audit trail are the records spec 005 deliberately did not
read; they make operations auditable and recoverable and they underpin status/history queries. They
are essential but layer on top of the create/destroy spine.

**Independent Test**: Run a create then a destroy; confirm each transition is reflected in the
environment record's status and that each dispatched run is captured in the audit trail with its
inputs, run id/url, and outcome — and confirm the control plane never uses the registry as the source
for "what's deployed" (that stays inventory/ARG).

**Acceptance Scenarios**:

1. **Given** any mutating verb, **When** it runs, **Then** the environment registry records its
   intent (env_id, kind, subscription, region, owner) and transitions its lifecycle status
   (`requested` → `provisioning` → `active` / `failed`, and `destroying` → `destroyed`).
2. **Given** any dispatched run, **When** it is dispatched and when it terminates, **Then** a
   provisioning-run record captures the dispatch inputs, GitHub run id and URL, the correlating
   `env_id`, timestamps, and the terminal outcome.
3. **Given** the question "what is deployed?", **When** it is answered, **Then** the answer is derived
   from inventory (Resource Graph), **not** the registry (Article III; division of truth) — the
   registry answers "what was intended / what happened."

---

### User Story 5 - Resilient completion tracking (webhook + polling reconcile) (Priority: P3)

The control plane tracks every dispatched run to a terminal outcome correlated by `env_id`. The
primary signal is the `workflow_run` **webhook**, received by a public-facing **reverse-proxy
ingress** that forwards to a **separate internal handler** with no direct public exposure; a
**polling reconciler** sweeps for terminal status so a missed webhook delivery never leaves a run
stuck "in flight."

**Why this priority**: Reliable tracking is what makes dispatch trustworthy, but the create/destroy
spine (US1/US2) already tracks runs; this story hardens that tracking against missed deliveries and
defines the ingress topology. It is robustness on top of working tracking.

**Independent Test**: Dispatch a run and confirm its outcome is captured; then simulate a missed
webhook delivery and confirm the polling reconciler still drives the run to its recorded terminal
status — no run remains perpetually in flight.

**Acceptance Scenarios**:

1. **Given** a dispatched run, **When** its `workflow_run` webhook is delivered, **Then** the
   inbound request is verified (GitHub signature) at the ingress, forwarded to the internal handler,
   correlated by `env_id`, and the run's terminal outcome is recorded.
2. **Given** a dispatched run whose webhook is never delivered, **When** the polling reconciler runs,
   **Then** it detects the run's terminal status and records the outcome — the run does not stay "in
   flight" indefinitely.
3. **Given** the deferred production hosting (Q1), **When** the MVP is demonstrated without a public
   ingress, **Then** the polling reconciler alone closes the tracking loop, and the webhook
   ingress→handler path is built and tested so spec 007 can host it with no logic change.

---

### Edge Cases

- **Failed create — no leaked allocation**: a `spoke create` whose dispatch or run fails MUST NOT
  leave a live IPAM allocation row; the provisional allocation is released/marked for cleanup and the
  verb is safely re-runnable.
- **Idempotent re-create**: re-invoking `create` for an existing `(subscription, name)` converges to
  the existing environment (matching spec 004 FR-009) rather than duplicating or double-allocating.
- **Concurrent op on same environment**: a mutating verb on an environment with a non-terminal
  in-flight run is rejected ("operation already in progress"), not dispatched as a second racing run
  (single-flight, FR-022a); read verbs stay available.
- **Destroy of an already-destroyed environment**: a clean no-op (no error, no spurious run).
- **Missed webhook**: polling reconciliation drives the run to terminal status (US5 AS2).
- **Duplicate/late webhook**: webhook handling is idempotent — a replayed or out-of-order delivery
  does not corrupt the recorded outcome.
- **Address exhaustion at allocate**: `spoke create` fails fast with a clear error and no partial
  allocation when the region's space cannot satisfy the requested size.
- **Registry/Azure divergence**: an environment marked `active` whose resources are absent from
  inventory (or vice versa) is surfaced as the inverse-direction (registry↔Azure) reconciliation
  question — recording the divergence is in scope; an automatic reconciliation loop is **not**
  (constitution: no reconciliation loop for resources created outside the platform).
- **Confirmation bypass attempt**: any path that would dispatch a destroy without explicit
  confirmation is rejected (Article VIII).
- **Control plane cannot reach Postgres**: a verb requiring the ledger/registry fails fast with a
  clear error rather than inventing address space or proceeding without recording intent.
- **GitHub App credential missing/expired**: dispatch fails fast with a clear error; no intent is
  left dangling in `provisioning` without a corresponding run record.

## Requirements *(mandatory)*

### Functional Requirements

#### Plane split & dispatch (Article II)

- **FR-001**: The control plane MUST expose every platform mutation as a **typed verb**; the CLI (and
  later the MCP) MUST invoke verbs only and MUST NOT generate, edit, or apply IaC at runtime (Article
  II). The verb layer MUST be the **single implementation** consumed by the `pdp` CLI now and the
  spec-007 MCP server later — no per-front-end reimplementation.
- **FR-002**: The control plane MUST NOT execute OpenTofu in-process. Every infrastructure mutation
  MUST be performed by **dispatching a GitHub Actions workflow** (GitHub App + `workflow_dispatch`) in
  the platform repo, which runs OpenTofu authenticated to Azure via **OIDC with no stored cloud
  secrets** (Articles I/II).
- **FR-003**: Each dispatched run MUST be correlated to its originating request by an **`env_id`** (and
  any additional correlation id) passed as a dispatch input, so the control plane can match workflow
  status back to the request that caused it.
- **FR-004**: The control plane MUST track each dispatched run to a **terminal outcome** (success /
  failure / cancelled) and record it. Tracking MUST be **resilient**: the primary signal is the
  `workflow_run` webhook, and a **polling reconciler** MUST catch runs whose webhook was missed so no
  run is left perpetually "in flight," within the bound stated in SC-006 (≤60s sweep, ~2 min settle).
- **FR-005**: The inbound webhook MUST be received by a **public-facing reverse-proxy ingress** that
  forwards to a **separate internal webhook-handler** with no direct public exposure (defense in
  depth). Inbound deliveries MUST be **verified** (GitHub signature) before processing, and handling
  MUST be **idempotent** against duplicate/late deliveries. (Production hosting of this topology is
  deferred to spec 007 — see FR-019.)

#### Plan & confirm (Article VIII)

- **FR-006**: Every mutating verb MUST surface a **plan** of the intended change **before** any apply
  is dispatched (Article VIII).
- **FR-007**: Every **destructive** verb (destroy, address release, peering removal) MUST require
  **explicit human confirmation** before dispatch — including, and especially, from chat/CLI. The
  confirmation MUST be unambiguous (e.g., restating the target) and MUST be **impossible to bypass
  silently** (Article VIII).

#### IPAM & Gate-G1 closure (Articles VI)

- **FR-008**: `spoke create` MUST **allocate the spoke's address block live by requested size from the
  IPAM ledger** at vend time (the control plane reaches the private Postgres ledger that CI cannot),
  writing the allocation **before** dispatch and passing the allocated CIDR as the workflow input —
  closing the spec-004 **Gate-G1** deferral. `spoke_cidr` MUST no longer be a hand-fitted typed input.
- **FR-009**: `spoke destroy` MUST **release the IPAM allocation row** after the destroy run succeeds,
  so the block becomes reusable — completing the teardown spec 004 deliberately left to the control
  plane. A failed/partial destroy MUST NOT release the row.
- **FR-010**: The control plane MUST expose **IPAM allocate / release / query** as verbs (spec 002),
  and **every** CIDR it hands to a workflow MUST come **only** from the ledger (Article VI) — never
  invented or hardcoded.
- **FR-011**: Address-space allocation/release MUST be **consistent** with intent recording and run
  outcome: a failed create MUST NOT leak a live allocation, and a successfully created environment MUST
  have **exactly one** live allocation row.

#### Verbs wrapping specs 2–5

- **FR-012**: The control plane MUST wrap, as typed verbs: **fabric create/destroy** (spec 003),
  **spoke create/destroy** (spec 004), **IPAM allocate/release/query** (spec 002), and
  **inventory/environment queries** (spec 005).
- **FR-012a**: Because no `workflow_dispatch` **fabric create** path exists today (the `westus3`
  fabric was first deployed via the GitOps `iac-apply`-on-merge path), this spec MUST **add a
  `fabric create` dispatch workflow** over the existing `infra/fabric` stack — mirroring `spoke-vend`
  / `fabric-destroy` — so **all** fabric/spoke verbs dispatch uniformly (Article II) and the verb
  layer honors its contract for every wrapped verb.
- **FR-013**: Inventory/environment queries MUST **reuse the existing `Pdp.ControlPlane.Inventory`
  component** (spec 005), injecting the control plane's own credential into its **pluggable
  credential seam** — the spec-005 hook — with **no** duplicated inventory logic.

#### Registry & audit trail (division of truth)

- **FR-014**: The control plane MUST persist an **environment registry** recording each environment's
  intent: at minimum `env_id`, kind (fabric / spoke; workload deferred to spec 008), target
  subscription, region, owner, and **lifecycle status** (`requested` / `provisioning` / `active` /
  `destroying` / `destroyed` / `failed`). `env_id` MUST be a **generated stable surrogate** (the
  registry primary key and the correlation key passed to/from workflows), and the registry MUST
  enforce a **unique natural key `(kind, subscription, name)`** so a given environment maps to exactly
  one surrogate (the basis for idempotent convergence, FR-022).
- **FR-015**: The control plane MUST persist a **provisioning-run audit trail**: one record per
  dispatched run (provision or destroy) capturing dispatch inputs, **GitHub run id and URL**, the
  correlating `env_id`, start/finish timestamps, and the terminal outcome.
- **FR-016**: **Division of truth MUST hold**: Azure Resource Graph (via inventory) is the truth for
  *what is deployed*; the registry records *intent* and the audit trail records *what happened*. The
  control plane MUST NOT treat the registry as the source for "what's deployed" (Article III);
  inventory still reads ARG.
- **FR-017**: The registry and audit trail MUST live in the **existing platform Postgres** (the
  IPAM-ledger flexible server), in a **new dedicated schema** separate from the IPAM ledger tables, in
  the same database.

#### CLI front-end

- **FR-018**: The control plane MUST be driven by a **`pdp` CLI** that exposes the verbs as commands
  and produces both **human-readable** output and machine-readable **`--json`** (a consistent,
  documented schema), so scripts and the future MCP consume the same results without parsing
  human-formatted text.

#### Scope, hosting & identity (Articles I, II, IX)

- **FR-019**: This spec MUST deliver the **verb layer, the registry/audit persistence, the
  run-tracking subsystem (webhook ingress + internal handler + polling reconciler), and the `pdp`
  CLI**, runnable under the **owner's context** (which can reach the private platform Postgres and
  GitHub). It MUST NOT provision the **production Azure hosting** — the ACA-hosted service, the public
  ingress endpoint, and the control-plane managed identity — which is **deferred to the spec-007 MCP
  host**; the components MUST be built so spec 007 hosts them with **no verb-logic change**. For the
  MVP demonstration, completion tracking is closed by the **polling reconciler** (no public ingress
  required); the webhook ingress→handler path is built and tested so it activates once hosted.
- **FR-020**: The control plane MUST use **least-privilege identities** and hold **no standing cloud
  write credential**: cloud mutations happen only in the OIDC-authenticated execution-plane workflow,
  not in-process. Cross-subscription **reads** (inventory/discovery) MUST use a read-only credential.
  The **GitHub App credential** (for `workflow_dispatch`) MUST be the only non-Azure secret
  (constitution Additional Constraints).
- **FR-021**: No **public endpoints** except the single justified **webhook ingress** (Article IX
  exception), and that endpoint MUST be a thin reverse proxy forwarding to a **non-public** handler;
  everything else MUST be **private by default**.

#### Observability

- **FR-O1**: The control plane MUST emit **structured logs and distributed traces (and basic
  operational metrics) correlated by `env_id`** across every verb invocation and every run-state
  transition (dispatch → tracking → terminal outcome), so a stuck or failed run is debuggable.
  Instrumentation MUST be built on the **Azure Monitor OpenTelemetry Distro** targeting **Application
  Insights**. The **Application Insights resource** is provisioned with the deferred hosting (spec
  007, FR-019) — this spec adds the instrumentation and exports to a dev/local sink for the MVP, so it
  introduces **no new Azure resource** of its own. **Azure-side** diagnostic routing, Azure Policy
  enforcement, and cost visibility remain **spec 010**.

#### Lifecycle, idempotency & teardown (Article IV)

- **FR-022**: Every create verb MUST be **idempotent/convergent**: re-invoking `create` for an
  existing environment converges (matching spec 004 FR-009) rather than duplicating or
  double-allocating; a destroy of an already-destroyed environment MUST be a clean **no-op**.
- **FR-022a**: Mutating verbs MUST be **single-flight per environment**: a mutating verb targeting an
  environment whose lifecycle status is **non-terminal** (a dispatched run is in flight) MUST be
  rejected with a clear "operation already in progress" error rather than dispatching a second run
  against the same environment. **Read** verbs (inventory, query, run status) MUST remain available at
  all times. (Idempotent re-create, FR-022, applies only when no run is in flight.)
- **FR-023**: A verb MUST **fail fast and cleanly** on invalid input (unregistered region, no deployed
  fabric, non-writable subscription, exhausted address space, unreachable Postgres, missing GitHub App
  credential) **before** recording intent or dispatching — leaving no leaked allocation and no orphaned
  registry row.
- **FR-024**: Every **resource-creating verb MUST have a clean teardown path** (Article IV): fabric
  destroy and spoke destroy (with allocation release, FR-009) leave no orphaned resources, no leaked
  allocations, and no dangling peerings, and transition the environment record to `destroyed`. The
  **spec's own footprint MUST be destroyable**: because production hosting is deferred (FR-019), this
  spec adds only the **registry schema + audit tables** in existing Postgres (droppable) and **no new
  Azure resources** — teardown is dropping the schema (the spec-007 host is torn down with spec 007).
- **FR-025**: A dispatched run that **fails** MUST leave the environment in a `failed` status with the
  run's id/url recorded and MUST NOT leak an IPAM allocation; the system MUST be **recoverable** by
  re-running the verb.

### Key Entities

- **Verb (action)**: a typed, deterministic platform operation — `fabric create/destroy`, `spoke
  create/destroy`, `ipam allocate/release/query`, `inventory` / `env` queries. The single unit of
  capability, implemented once and consumed by the CLI and (later) the MCP.
- **Environment record (registry)**: the recorded **intent** for one environment — `env_id`, kind,
  target subscription, region, owner, and lifecycle status. Postgres, not Azure-derived. Identified by
  a **generated stable surrogate `env_id`** (registry PK + workflow correlation key) with a **unique
  natural key `(kind, subscription, name)`** enforcing idempotent convergence.
- **Provisioning run (audit)**: one dispatched execution-plane run (provision or destroy) — dispatch
  inputs, GitHub run id/url, correlating `env_id`, timestamps, terminal outcome. The audit trail.
- **IPAM allocation (consumed)**: the spoke block allocated **live by size** from the ledger at vend
  (FR-008) and released on destroy (FR-009). The ledger's region `/16` remains the authority.
- **Correlation (`env_id`)**: the identifier tying a request → dispatched workflow run → recorded
  outcome.
- **Plan & confirmation**: the surfaced preview of an intended change and the explicit gate that must
  pass before apply (always) and destroy (with confirmation).
- **Run-tracking subsystem**: the **reverse-proxy webhook ingress** + **internal webhook handler** +
  **polling reconciler** that drive every dispatched run to a recorded terminal outcome.
- **`pdp` CLI invocation**: a command mapping to a verb, rendering human-readable or `--json` output
  from the same verb result.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The owner can vend a spoke **end to end with a single command** — no hand-fitted CIDR,
  no hand-run workflow — and the spoke appears in inventory; verified live against the `westus3`
  fabric.
- **SC-002**: `spoke create` allocates its block **live from the ledger by size** (Gate-G1 closed):
  100% of vended spokes have a ledger allocation row created **by the control plane** (not a typed
  input), within the region `/16` and non-overlapping (ledger-enforced).
- **SC-003**: After a successful `spoke destroy`, **zero** leaked allocation rows remain and the block
  is reusable by a later vend.
- **SC-004**: **Every** mutating verb surfaces a plan before apply and **every** destroy requires
  explicit confirmation; **zero** mutations dispatch without their plan/confirmation gate (Article
  VIII) — verifiable by attempting to bypass.
- **SC-005**: **Every** infrastructure mutation occurs **only** via a dispatched GitHub Actions
  workflow (OIDC, no stored cloud secrets); **zero** OpenTofu executions occur in-process (Article II)
  — verifiable from run logs.
- **SC-006**: **Every** dispatched run reaches a recorded terminal outcome correlated by `env_id`; a
  deliberately missed webhook is still reconciled to terminal status by polling — **no** run is left
  "in flight." The reconciler sweeps at least every **60 seconds**, and a missed-webhook run reaches
  its recorded terminal status within **~2 minutes** of the run finishing.
- **SC-007**: The registry records intent for **every** environment and the audit trail records
  **every** provisioning run (inputs, run id/url, outcome); the owner can answer "what did I ask for
  and what happened?" from the registry while "what's deployed?" still comes from inventory/ARG
  (division of truth).
- **SC-008**: **Every** verb is invocable from the `pdp` CLI with both human-readable and `--json`
  output carrying the same data; the `--json` is consumable without parsing human text.
- **SC-009**: The **same** verb implementation backs the CLI and is ready for the spec-007 MCP with
  **no** reimplementation (one verb layer); inventory/env verbs reuse the spec-005 component with the
  control plane's credential injected.
- **SC-010**: **Teardown** — fabric/spoke verbs tear down cleanly (allocation released, registry
  marked `destroyed`, no dangling peerings), and the spec's own footprint (the registry schema + audit
  tables in existing Postgres; **no new Azure resources**, hosting deferred to spec 007) can be removed
  cleanly (Article IV).
- **SC-011**: **No** public endpoint exists except the single justified webhook ingress (a reverse
  proxy forwarding to a non-public handler); the control plane holds **no** standing cloud write
  credential, and the **GitHub App** secret is the only non-Azure secret.
- **SC-012**: A verb **acknowledges and begins tracking** a dispatch within a small budget (target:
  dispatch confirmed **P95 under 5 seconds**), independent of the underlying IaC run duration; failure
  modes (invalid input, unreachable Postgres, missing credential) return a clear error in the same
  budget without dispatching.
- **SC-013**: For **every** verb invocation and run, the control plane emits structured logs and
  traces **correlated by `env_id`** (via the Azure Monitor OpenTelemetry Distro → Application
  Insights), such that a stuck or failed run can be traced end to end from a single `env_id` — with
  no new Azure resource provisioned by this spec (the App Insights resource ships with the spec-007
  host).

## Assumptions

- Specs **002–005 are merged and live**: the IPAM ledger (Postgres) and the `westus3` fabric are
  deployed, with `app1`/`app2` spokes vended; the dispatch-target workflows exist in the platform repo
  (`spoke-vend`, `spoke-destroy`, `fabric-destroy`, `iac-plan`/`iac-apply`). This spec **adds** a
  `fabric create` `workflow_dispatch` counterpart over the existing `infra/fabric` stack alongside
  `fabric-destroy` (the fabric was first deployed via the GitOps `iac-apply` path), so all
  fabric/spoke verbs dispatch uniformly (FR-012a).
- The control plane **can reach the private platform Postgres** (IPAM ledger + new registry schema) —
  the reachability CI lacks (the Gate-G1 premise). For the MVP it runs under the **owner's context**
  with that reachability, exactly as the spec-007 ACA host will later.
- **Production Azure hosting** (ACA service, public ingress, control-plane managed identity) is
  **spec 007** (Clarifications Q1); spec 006 builds host-ready components and demonstrates under the
  owner/dev context. Completion tracking for the MVP is closed by **polling** (Q2); the webhook
  ingress→handler path is built and tested for 007 to host.
- The **registry + audit trail** live in the existing Postgres in a **new schema** (Q3), distinct from
  the IPAM ledger tables.
- Run tracking is **webhook (reverse-proxy ingress → internal handler) + polling reconciliation**
  (Q2); webhook handling is idempotent and signature-verified.
- **Confirmation** for destructive verbs is an explicit step (e.g., restating the target or a
  plan-bound confirmation token); the exact CLI mechanism is a plan detail, but it MUST be
  unbypassable (Article VIII).
- The control plane authenticates to GitHub via the **GitHub App** (the only non-Azure secret) and to
  Azure for **reads** via a read-only credential; **writes happen only in the OIDC execution plane**,
  not in-process.
- **Workload** verbs/archetypes are **spec 008**; "environment kind" here covers **fabric** and
  **spoke**. `pdp-env` grouping of workloads is surfaced through inventory (spec 005), but workload
  provisioning is out of scope.
- **Division of truth** is preserved: the registry records intent and the audit trail records runs;
  "what's deployed" always comes from inventory/Resource Graph (Article III). Recording an
  intent↔Azure **divergence** is in scope; an automatic **reconciliation loop** is not.

### Out of scope (non-goals)

- The **MCP server** and the **production ACA hosting** — the public ingress deployment, and the
  control-plane managed identity (**spec 007**).
- **Workload archetypes / workload deployment** into spokes (**spec 008**).
- An automatic **registry↔Azure reconciliation loop** (recording divergence is in scope; a
  self-healing loop is not — constitution: no reconciliation loop for resources created outside the
  platform).
- **Multi-region ergonomics** (spec 009). **Azure-side** observability — diagnostic/log routing,
  Azure Policy enforcement, and per-environment cost visibility — remains **spec 010**. (Spec 006 owns
  only the control plane's **own** application telemetry: logs/traces correlated by `env_id` via Azure
  Monitor OTel → App Insights, FR-O1.)
- **Any in-process OpenTofu execution** — forbidden by Article II; all IaC runs in dispatched CI.
