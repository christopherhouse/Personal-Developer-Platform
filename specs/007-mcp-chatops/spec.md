# Feature Specification: MCP Chatops — Host the Control Plane & Operate the Platform Conversationally

**Feature Branch**: `007-mcp-chatops`

**Created**: 2026-06-18

**Status**: Draft (amended 2026-06-24 — US2: the MCP plan→confirm→apply flow is asynchronous, non-blocking, and owner-driven, with on-demand run reconciliation on the conversational surface)

**Input**: User description: "Spec 7 — mcp-chatops: host the control plane in Azure and operate the platform conversationally. Stand the spec-006 control plane up as a real, in-Azure service (Azure Container Apps, VNet-integrated, managed identity, App Insights) and add the `pdp-mcp` MCP server — a thin adapter over the same typed verbs — so the owner can vend/destroy spokes, query IPAM, ask 'what's deployed?', and read intent/run history conversationally, with the Article VIII plan/confirm gate surfaced through chat. Unblocks the spec-006 live acceptance (T071/T072). Binding: AVM-first, private & cheap, OpenTofu-only dispatched IaC, .NET 10, reuse spec-005/006 with no duplicated logic, no prohibited deps, destroyable by design. Non-goals: no new verbs, no APIM, no multi-user authz, no workload archetypes (spec 8), no second region (spec 9)."

## Overview

Spec 006 built PDP's **control plane** — the typed [verb](../../docs/glossary.md) layer (`fabric`, `spoke`,
`ipam`, `inventory`, `env`, `run`), the Postgres intent **registry** and provisioning-run audit trail,
the GitHub-App **dispatch** path, the `env_id`-correlated **run-tracking subsystem** (webhook ingress →
internal handler → polling reconciler), and the `pdp` CLI. But spec 006 stopped at the **owner's laptop
context**: the control plane only runs locally, against a **private, VNet-injected, Entra-only**
[IPAM ledger](../../docs/glossary.md) Postgres that the owner's machine cannot reach without VNet access
— so the platform **cannot be operated remotely**, and the spec-006 **live acceptance (quickstart
T071/T072) is blocked**.

This spec closes that gap with two coupled deliverables:

1. **Production hosting of the control plane** (the spec-006 deferrals). Run the existing
   `Pdp.ControlPlane.Api` (webhook handler + Wolverine durable inbox/outbox + polling reconciler) and
   `Pdp.ControlPlane.Ingress` (YARP reverse proxy) as **long-lived services on Azure Container Apps**
   (no APIM), **integrated into the control-plane VNet** (the VNet that hosts the private Postgres) so they
   have **line-of-sight to the private
   IPAM-ledger Postgres**. All Azure access — including **Entra (token) authentication to Postgres** —
   uses a **managed identity**: zero stored cloud secrets; the **pdp-orchestrator GitHub App private
   key remains the only non-Azure secret**. Provision the **Application Insights** resource that
   receives the `env_id`-correlated traces the control plane already emits (spec-006 FR-O1). The GitHub
   `workflow_run` **webhook ingress is the one sanctioned public endpoint** (Article IX).

2. **The `pdp-mcp` chatops server.** A new ASP.NET Core **MCP server** ([MCP](../../docs/glossary.md)
   C# SDK, streamable HTTP) that exposes the **same typed verbs as MCP tools** — **no reimplementation**,
   a thin adapter over the spec-006 verb layer, exactly as the `pdp` CLI is. An MCP client (e.g. Claude)
   can then drive the platform conversationally — vend/destroy spokes and fabrics, query IPAM, ask
   *"what's deployed?"* (inventory from Azure Resource Graph), and read the intent / run-audit history.
   The MCP endpoint is **gated by Entra auth so only the owner can call it**. The **Article VIII gate is
   surfaced through chat**: plan-before-apply and an **explicit, unbypassable confirm-before-destroy
   that restates the target name** — a chat request can never destroy without it.

**The verb layer is not touched.** Spec 006 built it as the single implementation behind both the CLI
and a future MCP; this spec adds the MCP front-end and the hosting around it, with **no new platform
capabilities or verbs**. The headline outcome it unblocks: the **spec-006 live acceptance (T071/T072)
becomes runnable**, because the control plane now executes **in-VNet with managed identity next to the
ledger**.

## Clarifications

### Session 2026-06-18

- Q: How should the `pdp-mcp` endpoint enforce that only the owner can call it? → A: **Server-validated
  Entra bearer JWT (OAuth 2.1 protected-resource).** The MCP client obtains an Entra access token for the
  `pdp-mcp` app registration; the server validates issuer/audience/signature and authorizes a single
  allow-listed owner object id (no APIM; auth handled by the server itself).
- Q: Where do the .NET service images live and how does ACA pull them secret-free? → A: **Private Azure
  Container Registry (Basic SKU).** CI pushes images to it over OIDC; each ACA app pulls using its own
  managed identity's `AcrPull` role — no stored registry credential. The ACR is a new, destroyable Azure
  resource torn down with the rest of the stack.
- Q: Where should the ACA / identity / App Insights / ACR resources live for clean, isolated teardown? →
  A: **A new adjacent stack `infra/control-plane-host`** (its own RG and state, e.g.
  `platform/control-plane-host`) that **consumes** the existing VNet, Postgres, and private DNS by
  reference — keeping the protected ledger RG (`prevent_destroy` + `CanNotDelete`) untouched and giving
  spec 7 an independently destroyable footprint. (Plan detail: the ACA subnet is declared by the
  VNet-owning stack and consumed by the host stack, to avoid subnet drift on the AVM VNet module.)
- Q: Shared identity or per-app for the hosted services? → A: **One user-assigned managed identity per
  hosted app** (Api, Ingress, MCP each get their own UAMI), each granted **only** what that app needs
  (least-privilege per app): e.g. the Api/MCP UAMIs are registered Entra principals on Postgres and hold
  Resource Graph Reader as their verbs require, while the reverse-proxy Ingress UAMI holds neither — only
  `AcrPull`. UAMIs are created and granted **ahead of** the apps and torn down with the host stack.
- Q: Which hosted apps stay always-on vs. scale-to-zero? → A: **Api + Ingress always-on (min 1 replica);
  MCP scale-to-zero.** The webhook Ingress and the reconciler-bearing Api must stay running to catch
  `workflow_run` deliveries and meet the spec-006 sweep bound (≤60s / ~2 min settle); the MCP server has no
  background duty, so it scales to zero and wakes on an authenticated call (cheapest reliable posture,
  Article IX).
- Q: How are the two public surfaces (GitHub webhook + MCP) exposed on ACA? → A: **One YARP ingress,
  both routes.** The YARP ingress is the **single app with external (public) ingress** and routes
  `/webhooks/github` → the **internal** Api webhook handler and `/mcp` (+
  `/.well-known/oauth-protected-resource`) → the **internal** MCP server. Both the Api and the MCP server
  use internal ingress; the Entra JWT is validated **at the MCP server** (YARP forwards the
  `Authorization` header, no business logic). Single public surface — tighter Article IX posture.

### Session 2026-06-24

- Q: The MCP `Plan*` tools return the moment the plan run is **dispatched** (no in-tool polling), before
  the plan has actually run — so the paired `Apply*` call races ahead of the plan being recorded
  successful and is rejected by the single-flight guard, and the owner "confirms" a plan they never saw.
  How should the chat plan→apply flow work? → A: **Asynchronous, non-blocking, owner-driven.** A `Plan*`
  tool dispatches the plan run and returns immediately (env_id, run handle, confirmation token) — **no
  tool call ever blocks** waiting for a GitHub Actions workflow to finish (the same dispatch-and-return
  posture `Apply*` already has). The owner reviews the surfaced plan (the captured plan output + run link)
  via the read tools once the plan run completes, then confirms; the `Apply*` tool dispatches the gated
  mutation only when the plan run has reached a **successful terminal outcome**.
- Q: How does the conversational surface learn a dispatched workflow has completed, given the MCP node is
  **stateless and scale-to-zero** (it cannot receive the `workflow_run` webhook), and **where** does that
  reconciliation live? → A: **Only in the status-check read tools.** Completion is discovered by
  correlating the run by its run-name and polling GitHub Actions; **only** the registry-read status tools
  (`ShowEnvironment` / `RunStatus`) reconcile run status **in-process, on demand, idempotently** before
  they read. The `Plan*` and `Apply*`/`Destroy*` tools **do not reconcile or poll** — `Plan*` is pure
  dispatch, and `Apply*`/`Destroy*` is a pure registry read + dispatch. The owner (or agent) therefore
  checks status between plan and apply — which **is** the Article VIII plan review — so the registry is
  already up to date when apply reads it. The Api node remains the background safety net (first-terminal-
  wins dedup makes both drivers safe to run concurrently).
- Q: When an `Apply*`/`Destroy*` is called before its plan run has succeeded, what does the owner see? →
  A: A **clear, distinct "the plan has not finished yet — check its status and retry once it has
  succeeded" response**, kept **separate** from the genuine single-flight rejection (a real concurrent
  mutating operation already in flight). A failed plan yields "the plan failed; nothing to apply." None of
  these block the call.
- Q: The confirmation token is single-use (removed on first validation). When `Apply*`/`Destroy*` is
  called **before the plan run has succeeded**, is the token consumed or preserved? → A: **Preserved.**
  The "plan not ready" and "plan failed" rejections MUST NOT consume the token; it is consumed **only**
  when a gated mutation is actually dispatched. This mirrors the token service already preserving the
  token on an operation/target mismatch, and prevents a slow or failed plan from burning the token and
  forcing a needless re-plan.
- Q: The token's ~5-minute TTL was sized for the old flow where the plan was shown at issue time; in the
  async flow the token is issued at **plan dispatch** and the plan then queues + runs (2–5 min) before it
  is reviewable, so 5 minutes can expire before review. What TTL? → A: **A fixed ~15-minute TTL from
  issue.** The token's security property is single-use + verbatim-target restatement; the TTL is only
  anti-replay hygiene. A single generous fixed window comfortably covers dispatch + plan + human review
  and keeps the in-memory token store simple (no coupling to the status tool). (`data-model.md §5` updated
  from ~5 min to ~15 min.)

## User Scenarios & Testing *(mandatory)*

### User Story 1 - The control plane runs in-Azure, in-VNet, reaching the private ledger (Priority: P1) 🎯 MVP

The control plane no longer needs the owner's laptop. `Pdp.ControlPlane.Api` and
`Pdp.ControlPlane.Ingress` run as long-lived **Azure Container Apps** integrated into the **platform
VNet**, authenticating to the **private IPAM-ledger Postgres via a managed identity and an Entra token**
(no password, no stored secret). From in-VNet, the hosted control plane can do what the laptop could not:
reach the private ledger, allocate/release CIDR, read/write the registry, dispatch workflows, and track
runs to terminal. This directly **unblocks the spec-006 live acceptance (T071/T072)**.

**Why this priority**: This is the foundational deliverable and the reason the spec exists — without
in-VNet hosting under a managed identity, nothing else (conversational ops, live acceptance) is possible.
On its own it converts the spec-006 control plane from "laptop-only, acceptance blocked" to "hosted,
acceptance runnable," which is independently valuable.

**Independent Test**: Deploy the hosting stack; confirm the hosted `Pdp.ControlPlane.Api` authenticates
to the private Postgres with its managed identity (Entra token, no password), reads the registry/ledger,
and that a verb invoked against the hosted control plane (e.g. via the `pdp` CLI pointed at the hosted
endpoint, or the spec-006 quickstart T071/T072 run) completes the validate → allocate → dispatch → track
spine end to end — something impossible from the laptop in spec 006.

**Acceptance Scenarios**:

1. **Given** the hosting stack is applied, **When** the control plane starts, **Then** `Pdp.ControlPlane.Api`
   and `Pdp.ControlPlane.Ingress` run as long-lived Container Apps inside the control-plane VNet, with
   line-of-sight to the private Postgres flexible server.
2. **Given** the hosted control plane, **When** it connects to Postgres, **Then** it authenticates as a
   **managed identity** using an **Entra access token** (no administrator password, no connection-string
   secret), and is authorized on the database as a registered Entra principal.
3. **Given** the hosted control plane, **When** it performs a mutating verb, **Then** it allocates from the
   private ledger, records intent in the registry, dispatches the GitHub Actions workflow, and tracks the
   run to a terminal outcome correlated by `env_id` — the full spec-006 spine, now executing in-VNet.
4. **Given** the spec-006 quickstart live-acceptance steps (T071/T072) that were deferred because the
   ledger was unreachable, **When** they are run against the hosted control plane, **Then** they are
   **runnable and pass** (live vend tracked to terminal against the `westus3` fabric).

---

### User Story 2 - Vend then destroy a spoke through a chat conversation, with the plan/confirm gate (Priority: P1)

The owner operates the platform by talking to an MCP client (e.g. Claude). They ask to vend a spoke; the
`pdp-mcp` server invokes the **same `spoke create` verb** the CLI uses, **surfaces the plan**, and only
proceeds on the owner's go-ahead. Later they ask to destroy it; the server **refuses to destroy until the
owner gives an explicit confirmation that restates the target spoke's name** — a conversational request
alone can never destroy. Every tool call is **gated by Entra auth** so only the owner can reach it.

**Why this priority**: This is the spec's measurable success criterion — *"the owner can vend and then
destroy a spoke end to end through an MCP conversation with the plan/confirm gate."* It is the
conversational payoff that the whole `pdp-mcp` adapter exists to deliver, and it exercises the Article VIII
guardrails through the new chat surface.

**Independent Test**: From an Entra-authenticated MCP client, vend a spoke — the plan tool returns
**immediately** with a confirmation token and a run handle (nothing blocks on the workflow); poll the run
until the plan completes and **review the surfaced plan output**; confirm with the token and the spoke
name; watch the apply tracked to terminal and see the spoke in inventory — then destroy it (observe the
destroy is refused without an explicit target-restating confirmation, supply it, watch the destroy tracked
to terminal and the IPAM allocation released) — all without touching the CLI, and with **no tool call ever
blocking** on a GitHub Actions run.

**Acceptance Scenarios**:

1. **Given** an MCP client authenticated to the Entra-gated `pdp-mcp` endpoint, **When** the owner requests
   a spoke vend, **Then** the server invokes the spec-006 `spoke create` plan verb (no reimplementation),
   **dispatches the plan run and returns immediately** with the env_id, a run handle, and a single-use
   confirmation token — the call **does not block** waiting for the plan to finish, and **nothing is
   applied** (Article VIII).
2. **Given** a dispatched plan run, **When** the owner asks for its status, **Then** the read tools
   **reconcile the run on demand** and, once it has completed, surface the **captured plan output and the
   GitHub run link** for review — the plan the owner reviews is the actual `tofu plan`, not an empty
   placeholder.
3. **Given** a plan run that has completed successfully and been reviewed, **When** the owner confirms with
   the confirmation token and the verbatim spoke name, **Then** the server dispatches the gated apply and
   returns the tracked run handle immediately; when the run completes, the conversation can report the
   terminal outcome (tracked by `env_id`) and the new spoke is discoverable via the inventory tool.
4. **Given** a plan run that has **not yet** reached a successful terminal outcome, **When** the owner calls
   apply, **Then** the call is rejected with a **clear "the plan has not finished yet — check its status and
   retry once it has succeeded" response** (and "the plan failed; nothing to apply" if it failed) — **kept
   distinct** from the single-flight rejection raised when a genuine concurrent mutating operation is already
   in flight, and **without blocking** the call.
5. **Given** a request to destroy a spoke via chat, **When** the destroy tool is invoked, **Then** it does
   **not** dispatch until the owner supplies an **explicit confirmation that restates the target spoke's
   name**; the confirmation step is **impossible to bypass** from chat (Article VIII), and a bare "destroy
   it" without the restated target is refused. (The destroy plan→confirm flow is asynchronous and
   non-blocking in the same way as vend: `PlanSpokeDestroy` dispatches the destroy-plan run and returns a
   token immediately; `DestroySpoke` dispatches the gated destroy only once that plan run has succeeded.)
6. **Given** a confirmed destroy through chat, **When** the destroy run succeeds, **Then** the IPAM
   allocation is released, the environment record is marked `destroyed`, and the run outcome is recorded —
   identical behavior to the CLI path, because the same verb backs both.
7. **Given** an unauthenticated or non-owner caller, **When** it attempts any MCP tool call, **Then** the
   `pdp-mcp` endpoint rejects it (Entra gate) — only the owner can operate the platform conversationally.

---

### User Story 3 - Conversational answers: "what's deployed?", IPAM, and intent/run history (Priority: P2)

The owner asks the platform questions in chat and gets accurate answers: *"what's deployed?"* (inventory
from Azure Resource Graph, the live truth), *"what address space is allocated in westus3?"* (IPAM query),
and *"what did I ask for and what happened?"* (the intent registry and provisioning-run audit trail). These
are **read verbs** exposed as MCP tools over the same spec-005 inventory component and spec-006 registry —
no duplicated query logic.

**Why this priority**: Conversational read access is the everyday value of chatops and the natural
companion to the mutate path (US2), but the platform is already operable and acceptance already unblocked
without it. It layers breadth on the proven mutate/host core.

**Independent Test**: From the MCP client, ask the inventory, IPAM-query, and env/run-history questions;
confirm each returns correct data sourced from the live Azure Resource Graph (for "what's deployed") and
from the Postgres registry/audit trail (for "what I asked for / what happened"), reusing the spec-005/006
components with no reimplementation, and that division of truth holds (deployed ⇒ ARG, intent ⇒ registry).

**Acceptance Scenarios**:

1. **Given** the MCP client, **When** the owner asks *"what's deployed?"*, **Then** the answer is derived
   from **inventory (Azure Resource Graph)** via the reused `Pdp.ControlPlane.Inventory` component (spec
   005), never from the registry (Article III; division of truth).
2. **Given** the MCP client, **When** the owner queries IPAM (e.g. allocations in a region), **Then** the
   answer comes from the **IPAM ledger** read verb (spec 002/006).
3. **Given** the MCP client, **When** the owner asks *"what did I ask for and what happened?"*, **Then** the
   answer comes from the **intent registry + provisioning-run audit trail** (spec 006), distinct from the
   "what's deployed" answer.
4. **Given** any read verb exposed via MCP, **When** it runs, **Then** it uses the **same verb
   implementation** as the CLI with **no duplicated query/inventory logic**.

---

### User Story 4 - env_id-correlated telemetry is visible in Application Insights (Priority: P2)

The control plane already emits structured logs and `env_id`-correlated traces (spec-006 FR-O1) via the
Azure Monitor OpenTelemetry distro, but spec 006 had no Azure sink — it exported to a dev/local sink. This
spec **provisions the Application Insights resource** (and its backing Log Analytics workspace) that
receives that telemetry, so a stuck or failed run is traceable end to end from a single `env_id` in a real
Azure sink.

**Why this priority**: Operability telemetry is essential for trusting a remotely-operated platform, but
the platform functions and is demonstrable (US1–US3) before the Azure sink exists; this completes the
spec-006 observability deferral rather than enabling the core flow.

**Independent Test**: Trigger a verb/run through the hosted control plane, then query Application Insights
for the run's `env_id`; confirm the correlated logs and traces (verb invocation → dispatch → tracking →
terminal outcome) are present and stitched together by that single `env_id`.

**Acceptance Scenarios**:

1. **Given** the hosting stack is applied, **When** it completes, **Then** an Application Insights resource
   (with its Log Analytics workspace) exists to receive the control plane's telemetry.
2. **Given** the hosted control plane configured to export to that resource, **When** a verb is invoked and
   its run tracked, **Then** the `env_id`-correlated logs and traces appear in Application Insights.
3. **Given** a single `env_id`, **When** an operator queries Application Insights, **Then** the verb
   invocation, dispatch, run-state transitions, and terminal outcome can be traced end to end from that one
   identifier.

---

### User Story 5 - The whole spec-7 footprint tears down cleanly (Priority: P3)

Everything this spec creates in Azure — the Container Apps environment and apps, the managed identity, the
Application Insights + Log Analytics resources, any container image registry, and the ACA subnet — is
destroyable by a single dispatched `tofu destroy` over the hosting stack, plus any schema/role teardown,
leaving **zero residual footprint** beyond the deliberately protected pre-existing ledger.

**Why this priority**: Destroyable-by-design is a constitutional requirement (Article IV) and an explicit
acceptance criterion, but it is validated last because it depends on the hosting (US1) existing to be torn
down. It protects the personal platform's cost posture.

**Independent Test**: After demonstrating US1–US4, dispatch `tofu destroy` over the hosting stack and run
any role/grant teardown; confirm via Resource Graph that none of the spec-7-created resources remain, that
the pre-existing ledger/registry (protected by `prevent_destroy` + `CanNotDelete`) is **untouched**, and
that no leaked allocations, dangling role grants, or orphaned identities remain.

**Acceptance Scenarios**:

1. **Given** the spec-7 hosting stack is deployed, **When** `tofu destroy` is dispatched over it, **Then**
   the Container Apps environment + apps, managed identity, Application Insights + Log Analytics, image
   registry (if any), and ACA subnet are removed, with no orphaned resources.
2. **Given** teardown, **When** it completes, **Then** the pre-existing control-plane Postgres (IPAM ledger
   + registry schema), protected by `prevent_destroy` and the `CanNotDelete` lock, is **untouched** — spec 7
   does not destroy spec 2/6 state.
3. **Given** teardown, **When** it completes, **Then** the managed identity's Postgres role/grant and any
   Entra app registration created for MCP auth are cleaned up (or documented as the single reviewed manual
   step), leaving no dangling identity with standing access.

---

### Edge Cases

- **Managed identity not yet authorized on Postgres**: if the control-plane managed identity is not a
  registered Entra principal on the flexible server, the hosted service fails fast with a clear auth error
  rather than silently degrading — and the grant is part of the deploy path, not a hidden manual step.
- **The YARP ingress is the only inbound public path**: any attempt to reach the internal Api webhook
  handler, the internal MCP server, the reconciler, or Postgres directly from the public internet is
  refused; only the YARP ingress is publicly reachable, and it forwards the webhook and MCP paths to
  non-public internal apps (defense in depth, spec-006 FR-005/FR-021).
- **MCP endpoint reachable but Entra gate rejects**: a caller that reaches the MCP endpoint without a valid
  owner token gets an auth rejection; the endpoint never executes a verb for a non-owner (single-owner
  authz; no multi-user roles — that is a non-goal).
- **Confirm-before-destroy bypass attempt through chat**: any phrasing that would dispatch a destroy
  without the explicit, target-restating confirmation is refused (Article VIII) — including indirect or
  "just do it" requests.
- **Apply called before the plan run finishes**: an `Apply*`/`Destroy*` invoked while its plan run is still
  in flight is rejected with a **distinct, retryable "the plan has not finished yet" response** — never the
  misleading single-flight rejection (which is reserved for a genuine concurrent mutating operation), and
  never by blocking the call until the workflow finishes. A plan run that **failed** yields "the plan
  failed; nothing to apply." The genuine single-flight guard (a real second mutation against a non-terminal
  environment) remains in force and distinguishable.
- **Completion discovery without the Api node / a missed webhook**: because the MCP server is stateless and
  scale-to-zero it cannot receive the `workflow_run` webhook; the conversational surface MUST still observe
  a dispatched run reaching terminal. The **status-check read tools** (`ShowEnvironment` / `RunStatus`)
  **reconcile on demand** (correlated by run-name, polling GitHub Actions, idempotent first-terminal-wins)
  before they read, so the chat flow advances runs by itself even if the always-on Api node's background
  reconciler or webhook is unavailable. The `Plan*` / `Apply*` / `Destroy*` tools do **not** reconcile.
- **Scale-to-zero vs. always-on tracking**: the webhook handler and polling reconciler must remain
  reachable/running to catch `workflow_run` deliveries and sweep for missed ones within the spec-006 bound
  (≤60s sweep, ~2 min settle); a scale-to-zero configuration that would drop webhooks or stall the
  reconciler is a misconfiguration.
- **Image pull without a stored secret**: the Container Apps must pull their images using the managed
  identity (or an equivalently secret-free path); no registry password may be stored as a secret, to keep
  the GitHub App key the only non-Azure secret.
- **Cold MCP start surfaces no partial state**: if the MCP server is scaled to zero and a tool call wakes
  it, the call either completes correctly or fails cleanly — it never half-applies a mutation.
- **Telemetry sink unavailable**: if Application Insights is unreachable, the control plane still operates
  (telemetry export failure is non-fatal); observability degrades, operations do not.

## Requirements *(mandatory)*

### Functional Requirements

#### Production hosting of the control plane (Articles I, IX; spec-006 FR-019 closure)

- **FR-001**: This spec MUST host the existing `Pdp.ControlPlane.Api` (webhook handler + Wolverine durable
  inbox/outbox + polling reconciler) and `Pdp.ControlPlane.Ingress` (YARP reverse proxy) as **long-lived
  services on Azure Container Apps** — **no APIM** — with **no change to the spec-006 verb logic**
  (host-ready components, spec-006 FR-019).
- **FR-002**: The hosted services MUST be **integrated into the control-plane VNet**
  (`vnet-pdp-westus3-controlplane`, `10.0.0.0/24` — the VNet that hosts the private Postgres, **not** the
  hub fabric VNet) so they have **line-of-sight to the private, VNet-injected IPAM-ledger / registry
  Postgres** that the owner's laptop cannot reach (the reachability gap that blocked spec-006 live
  acceptance).
- **FR-003**: The Container Apps environment MUST be **private by default** (Article IX): the **YARP
  ingress is the single app with external (public) ingress** and is the **only publicly reachable
  surface**. It exposes exactly two public routes — (a) the **`workflow_run` webhook** path (the Article IX
  exception, spec-006 FR-021) and (b) the **Entra-gated MCP** path (`/mcp` + the
  `/.well-known/oauth-protected-resource` discovery endpoint, FR-009). The webhook-handler **Api**, the
  **MCP server**, the reconciler, Postgres, and every other component MUST use **internal ingress** (or no
  ingress) and MUST NOT be directly publicly reachable — they are reachable only via the YARP ingress
  inside the environment.
- **FR-004**: The ingress topology MUST be a **single public-facing reverse-proxy (YARP) app routing to
  internal apps** (preserving and extending spec-006's design): the YARP ingress forwards the GitHub
  webhook path to the **internal Api webhook handler** (which verifies the GitHub signature) and the MCP
  path to the **internal MCP server**, with **no business logic in YARP** (it forwards; it does not
  validate the Entra JWT — that happens at the MCP server). The **polling reconciler** continues to sweep
  for missed webhook deliveries within the spec-006 bound (≤60s sweep, ~2 min settle, spec-006 SC-006).
  The **YARP ingress and the reconciler-bearing Api MUST stay always-on (min 1 replica)** so deliveries
  are caught and the sweep runs; the **MCP server MAY scale to zero** (no background duty; ACA wakes it
  from zero on inbound traffic through YARP) provided a cold start never half-applies a mutation.

#### Identity & secret posture (Article IX; division of execution)

- **FR-005**: All Azure access by the hosted control plane MUST use **user-assigned managed identities,
  one per hosted app** (Api, Ingress, MCP), each granted **only** what that app needs (least-privilege per
  app) — including **Entra (token) authentication to the private Postgres** (no administrator password, no
  connection-string secret) and **read-only Azure Resource Graph** access for the apps whose verbs require
  it. The control plane MUST hold **no standing cloud write credential**; infrastructure writes still
  happen only in the OIDC-authenticated execution plane (spec-006 FR-002/FR-020).
- **FR-006**: Each hosted app's managed identity MUST be granted, as part of the deploy path, **only** the
  access that app requires: the app(s) that touch the ledger/registry MUST be **registered as Entra
  principals on the Postgres flexible server** with least-privilege database roles (the `infra/control-plane`
  stack added the owner as Entra admin and explicitly deferred the control-plane identity to "when a runtime
  exists" — that runtime is this spec); the reverse-proxy Ingress identity MUST hold **neither** Postgres
  nor Resource Graph access. Service images MUST live in a **private Azure Container Registry (Basic SKU)**
  to which CI pushes over **OIDC**; each app MUST pull its image using its identity's **`AcrPull`** role —
  **no registry password stored as a secret**.
- **FR-007**: The **pdp-orchestrator GitHub App private key** (for `workflow_dispatch`) MUST remain the
  **only non-Azure secret** in the system. No new long-lived cloud secrets may be introduced.

#### Observability sink (spec-006 FR-O1 closure)

- **FR-008**: This spec MUST **provision the Application Insights resource** (and its backing Log Analytics
  workspace) that receives the **`env_id`-correlated logs and traces** the control plane already emits via
  the Azure Monitor OpenTelemetry distro (spec-006 FR-O1). The hosted control plane MUST be configured to
  export its telemetry to that resource, so a run is traceable end to end from a single `env_id`. Telemetry
  export failure MUST be non-fatal to platform operations.

#### The pdp-mcp chatops server (Article II)

- **FR-009**: This spec MUST add a new ASP.NET Core **MCP server (`pdp-mcp`)** using the **MCP C# SDK over
  streamable HTTP**, hosted alongside the control plane, that exposes the **spec-006 typed verbs as MCP
  tools**. The MCP server MUST use **internal ingress** and be reached publicly **only via the YARP
  ingress** (FR-004). The MCP endpoint MUST be **gated by Entra authentication**, implemented as an
  **OAuth 2.1 protected resource**: the **MCP server itself** (not YARP) MUST validate the **Entra bearer
  JWT** (issuer / audience / signature) presented by the MCP client and authorize a **single allow-listed
  owner object id** (`oid` claim), so that **only the owner** can invoke any tool (single-owner authz;
  multi-user roles are a non-goal). Auth MUST be handled by the server itself — **no APIM**.
- **FR-010**: `pdp-mcp` MUST be a **thin adapter over the spec-006 verb layer** — exactly as the `pdp` CLI
  is — invoking the **same single verb implementation** with **no reimplementation** and **no new platform
  verbs/capabilities**. Adding the MCP front-end MUST NOT duplicate verb, dispatch, IPAM, registry, or
  inventory logic.
- **FR-011**: `pdp-mcp` MUST expose, as MCP tools, the platform operations needed for conversational
  operation: **vend/destroy spokes and fabrics**, **IPAM query**, **inventory ("what's deployed?")** from
  Azure Resource Graph, and **intent / run-audit history** reads. It MUST NOT extend the verb surface
  beyond what spec 006 implemented (hosting + adapter only).

#### Article VIII gate surfaced through chat

- **FR-012**: Every **mutating** verb invoked through `pdp-mcp` MUST **surface its plan before any apply is
  dispatched** (Article VIII; spec-006 FR-006), so the owner sees the intended change in the conversation
  before it proceeds. The plan the owner reviews MUST be the **actual captured plan output** (the `tofu
  plan`) with its run link — not an empty or placeholder result.
- **FR-013**: Every **destructive** verb invoked through `pdp-mcp` MUST require an **explicit, unbypassable
  confirmation that restates the target's name** before it dispatches (Article VIII; spec-006 FR-007). A
  conversational request **can never destroy** without that confirmation — there MUST be no chat phrasing,
  default, or shortcut that dispatches a destroy without the restated-target confirmation step.
- **FR-018**: The chat plan→confirm→apply flow MUST be **asynchronous and non-blocking**: a `Plan*` tool
  MUST **dispatch the plan run and return immediately** (env_id, run handle, single-use confirmation token),
  and an `Apply*`/`Destroy*` tool MUST **dispatch the gated mutation and return immediately** with the
  tracked run handle. **No MCP tool call may block** waiting for a GitHub Actions workflow to complete. The
  owner reviews the surfaced plan (FR-012) via the read tools between the plan and the confirm; an
  `Apply*`/`Destroy*` MUST dispatch the gated mutation **only when the plan run has reached a successful
  terminal outcome**.
- **FR-019**: When an `Apply*`/`Destroy*` is invoked while its plan run has **not yet reached a successful
  terminal outcome**, the MCP surface MUST reject it with a **clear, retryable response distinct from the
  single-flight rejection**: "the plan has not finished yet — check its status and retry once it has
  succeeded" while the plan is still in flight, and "the plan failed; nothing to apply" when it failed. The
  genuine single-flight rejection (a real concurrent mutating operation already in flight against a
  non-terminal environment) MUST remain in force and MUST be distinguishable from "plan not ready." This
  is presentation/sequencing on the adapter — it introduces **no new verb** and does not change the verb
  layer's single-flight invariant. The single-use confirmation token MUST be **preserved** (not consumed)
  on both the "plan not ready" and "plan failed" rejections — it is consumed **only** when a gated mutation
  is actually dispatched — so a slow or failed plan never forces a needless re-plan (consistent with the
  token service already preserving the token on an operation/target mismatch).
- **FR-020**: The conversational surface MUST be able to **observe a dispatched run reach a recorded
  terminal outcome without depending on the always-on Api node**. Because the MCP server is stateless and
  scale-to-zero (it cannot receive the `workflow_run` webhook), the **status-check read tools**
  (`ShowEnvironment` / `RunStatus`) MUST **reconcile run status on demand** — in-process, correlated by
  run-name, polling the execution plane, and **idempotent (first-terminal-wins)** so it is safe to run
  concurrently with the Api node's background reconciler/webhook. Reconciliation lives **only** in these
  read tools: the `Plan*` and `Apply*`/`Destroy*` tools MUST NOT reconcile or poll — `Plan*` is pure
  dispatch, and `Apply*`/`Destroy*` is a pure registry read + dispatch that relies on a prior status read
  having recorded the plan's terminal outcome (which is also the Article VIII review step). This reuses the
  existing spec-006 run-tracking/reconcile logic (**no reimplementation**, no new verb); the Api node
  remains the background safety net.

#### Reuse, IaC discipline & teardown (Articles I, IV, V, X)

- **FR-014**: All Azure resources this spec creates MUST be provisioned via **OpenTofu dispatched through
  GitHub Actions** (no in-process `tofu`, no portal/`az` mutations), **AVM-first** with recorded
  justification for any hand-rolled module (Article V), at the **smallest viable SKU** (Article IX). The
  ACA subnet MUST come from the **existing seeded control-plane VNet reservation** (`10.0.0.0/24`), not a
  newly invented range (Article VI). The spec-7 Azure resources MUST live in a **new adjacent stack
  (`infra/control-plane-host`) with its own RG and OpenTofu state**, consuming the existing VNet, Postgres,
  and private DNS by reference — so teardown is **isolated** and the protected ledger RG
  (`prevent_destroy` + `CanNotDelete`) is never in this stack's destroy scope.
- **FR-015**: This spec MUST **reuse the spec-005 inventory component and the spec-006 verb layer with no
  duplicated logic** (Article X; spec-006 FR-013), and MUST introduce **no prohibited dependencies**
  (MediatR, MassTransit, AutoMapper, Moq, Serilog, FluentAssertions v8+).
- **FR-016**: **Destroyable by design** (Article IV): the spec MUST include **clean teardown of every Azure
  resource it creates** (Container Apps environment + apps, the per-app managed identities, Application
  Insights + Log Analytics workspace, the Azure Container Registry, ACA subnet) via a dispatched `tofu destroy`, leaving **zero
  residual footprint** — and the teardown MUST **not** destroy the pre-existing control-plane Postgres
  (ledger + registry), which remains protected by `prevent_destroy` + the `CanNotDelete` lock. Any role
  grant / Entra app registration created for the per-app managed identities or MCP auth MUST be cleaned up
  (or documented as the single reviewed manual step).

#### Unblocking the spec-006 live acceptance

- **FR-017**: Once the control plane is hosted in-VNet under its managed identity (FR-001–FR-006), the
  **spec-006 live acceptance steps T071/T072** (deferred because the laptop could not reach the ledger)
  MUST be **runnable**: a live spoke vend (and destroy) against the `westus3` fabric, tracked to a terminal
  outcome by the hosted control plane.

### Key Entities

- **Hosted control plane**: the spec-006 `Pdp.ControlPlane.Api` + `Pdp.ControlPlane.Ingress` running as
  long-lived Azure Container Apps inside the control-plane VNet — the same code, now in-Azure, reaching the
  private ledger.
- **Control-plane managed identities (per-app)**: one user-assigned managed identity per hosted app
  (Api, Ingress, MCP), each secret-free and granted **only** what that app needs — Entra-token auth to
  Postgres and read-only Resource Graph for the apps whose verbs require it (registered as Entra principals
  on the flexible server with least-privilege roles), `AcrPull` for image pull on each. The reverse-proxy
  Ingress identity holds neither Postgres nor Resource Graph access.
- **`pdp-mcp` server**: the new ASP.NET Core MCP server (streamable HTTP, **internal ingress**) exposing
  the spec-006 verbs as Entra-gated MCP tools, reached publicly only via the YARP ingress; a thin adapter,
  the conversational sibling of the `pdp` CLI.
- **MCP tool ⇄ verb mapping**: each MCP tool is a 1:1 surface over an existing typed verb (vend/destroy,
  IPAM query, inventory, intent/run history) — no new capability.
- **Application Insights sink**: the Azure resource (with Log Analytics workspace) that receives the
  control plane's `env_id`-correlated logs and traces.
- **Public surface**: exactly **one** — the **YARP ingress** (the sole external-ingress app), routing two
  public paths to internal apps: the **`workflow_run` webhook** (Article IX exception) → internal Api, and
  the **Entra-gated MCP** path → internal MCP server. Everything else is internal/private.
- **Chat-surfaced plan/confirm gate**: the conversational rendering of Article VIII — plan-before-apply and
  the explicit, target-restating, unbypassable confirm-before-destroy.
- **Spec-7 Azure footprint (destroyable)**: Container Apps environment + apps, managed identity, App
  Insights + Log Analytics, any image registry, ACA subnet — all torn down by a dispatched `tofu destroy`,
  distinct from the protected pre-existing ledger.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: The control plane runs as **long-lived, VNet-integrated Azure Container Apps** and
  **authenticates to the private Postgres with a managed identity (Entra token, no password/secret)** —
  verifiable by the hosted service reading the ledger/registry that the laptop could not reach.
- **SC-002**: The owner can **vend a spoke and then destroy it end to end entirely through an MCP
  conversation**, with the **plan surfaced before apply** and an **explicit, target-restating confirmation
  required before destroy** — **zero** destroys dispatch from chat without that confirmation (verifiable by
  attempting to bypass).
- **SC-003**: **100%** of `pdp-mcp` tool calls execute the **same spec-006 verb implementation** as the CLI
  — **zero** duplicated verb/dispatch/IPAM/registry/inventory logic, and **zero** new platform verbs beyond
  the hosting + adapter.
- **SC-004**: Conversational reads are correctly sourced: *"what's deployed?"* comes from **inventory
  (Azure Resource Graph)** and *"what did I ask for / what happened?"* from the **registry/audit trail** —
  division of truth holds (Article III).
- **SC-005**: The **only** publicly reachable surface is the **YARP ingress** (the single external-ingress
  app), exposing exactly two routes — the webhook path and the Entra-gated MCP path; every other component
  (the Api webhook handler, the MCP server, the reconciler, Postgres) uses internal ingress and is **not**
  directly publicly reachable — verifiable by probing.
- **SC-006**: A **non-owner / unauthenticated** caller to the MCP endpoint is **always rejected**; **zero**
  verbs execute for a non-owner.
- **SC-007**: The control plane holds **no standing cloud write credential** and **no stored cloud secret**;
  the **GitHub App key is the only non-Azure secret**, and **no registry password** is stored — verifiable
  from the deployed configuration.
- **SC-008**: For a given run, the operator can **trace it end to end from a single `env_id` in Application
  Insights** (verb invocation → dispatch → run-state transitions → terminal outcome).
- **SC-009**: The **spec-006 live acceptance T071/T072** is **runnable and passes** against the hosted
  control plane — a live spoke vend/destroy against the `westus3` fabric tracked to terminal.
- **SC-010**: **Teardown** — a dispatched `tofu destroy` over the hosting stack leaves **zero** spec-7
  Azure resources (verifiable via Resource Graph), the **pre-existing ledger/registry is untouched**, and
  **no** dangling managed-identity grants or orphaned Entra registrations remain (Article IV).
- **SC-011**: **All** spec-7 Azure resources are provisioned by **OpenTofu dispatched through GitHub
  Actions** (AVM-first, smallest viable SKU); **zero** portal/`az` mutations and **zero** in-process `tofu`
  runs occur (verifiable from run logs), and the ACA subnet is carved from the existing seeded VNet
  reservation (no invented range).
- **SC-012**: The chat plan→confirm→apply flow is **non-blocking and self-advancing**: **no** MCP tool call
  blocks waiting for a workflow to finish; an apply issued before its plan has succeeded returns the
  **"plan not ready" response, never the single-flight rejection**; and the conversational surface observes
  a dispatched run reach terminal **on demand** even when the always-on Api node's background reconciler /
  webhook is unavailable — verifiable by planning, polling to a reviewed plan, confirming, and reaching a
  terminal apply entirely through chat with the Api reconciler disabled.

## Assumptions

- **Spec 006 is merged and green** (PR #22, 2026-06-18): the verb layer, registry/audit, dispatch,
  run-tracking subsystem (webhook ingress + internal handler + polling reconciler), and `pdp` CLI exist and
  are tested; this spec **hosts** them and adds the MCP adapter with **no verb-logic change**. Its live
  acceptance (T071/T072) is the deferral this spec unblocks.
- The **`infra/control-plane` stack already provisions** the private VNet (`10.0.0.0/24`, with a delegated
  `/28` Postgres subnet), the private DNS link, and the Entra-only Postgres flexible server, with the
  **control-plane managed identity explicitly deferred to "when a runtime exists"** — i.e. this spec. This
  spec adds a **new adjacent stack `infra/control-plane-host`** (own RG + state) with the ACA
  environment/apps, the managed identity + its Postgres role grant, App Insights + Log Analytics, and the
  ACR — **consuming** the existing VNet/Postgres/DNS by reference (Clarifications 2026-06-18). The ACA
  subnet is carved from the existing `10.0.0.0/24` reservation and is best **declared by the VNet-owning
  `infra/control-plane` stack and consumed by the host stack** (avoiding AVM VNet-module subnet drift). The
  pre-existing ledger RG stays protected by `prevent_destroy` + `CanNotDelete` and is never in the host
  stack's destroy scope.
- **MCP transport is streamable HTTP** (architecture.md: ASP.NET Core MCP server, streamable HTTP, on ACA,
  no APIM). The MCP endpoint's Entra auth is an **OAuth 2.1 protected resource**: the server validates the
  Entra bearer JWT and authorizes a single allow-listed owner object id (Clarifications 2026-06-18, FR-009).
  The precise SDK wiring (e.g. the app-registration scopes and protected-resource-metadata endpoint) is a
  `/speckit-plan` detail; the binding requirement is server-side JWT validation, single-owner, no APIM.
- **Container images** for the .NET services are built and published by CI (GitHub Actions) to a **private
  Azure Container Registry (Basic SKU)** over OIDC; the runtime pulls them using the **managed identity's
  `AcrPull`** role, so no registry password is stored (Clarifications 2026-06-18, FR-006). The ACR is a new,
  destroyable Azure resource (FR-016).
- **Scale posture** (Clarifications 2026-06-18): the **Ingress and the reconciler-bearing Api stay
  always-on (min 1 replica)** so `workflow_run` deliveries are caught and the polling reconciler meets the
  spec-006 bound; the **MCP server scales to zero** (architecture.md) and wakes on an authenticated call,
  provided a cold start never half-applies a mutation.
- **Division of truth** is preserved (Article III): "what's deployed" always comes from inventory/Azure
  Resource Graph; the registry records intent and the audit trail records runs.
- **Single owner**: there is exactly one operator; no multi-tenant/multi-user authorization beyond the owner
  is built (explicit non-goal).
- **Live platform context**: the platform runs in **westus3**; specs 002–006 are merged (IPAM ledger +
  westus3 fabric + app1/app2 spokes + control plane). The pdp-orchestrator GitHub App is set up.

### Out of scope (non-goals)

- **Any new platform capability or verb** beyond hosting + the MCP adapter — the verb surface is exactly
  what spec 006 implemented.
- **APIM** in front of the MCP/control plane — auth is handled by the server itself (Entra), per
  architecture.md.
- **Multi-tenant / multi-user authorization** — single-owner only; no roles, no delegation.
- **Workload archetypes / workload deployment** into spokes — **spec 008**.
- **A second region** or multi-region ergonomics — **spec 009**.
- **Azure-side observability** beyond the App Insights sink for the control plane's own telemetry —
  diagnostic/log routing, Azure Policy enforcement, and per-environment cost visibility remain **spec 010**.
- **Any in-process OpenTofu execution** — forbidden by Article II; all IaC runs in dispatched CI.
