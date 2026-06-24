# Implementation Plan: MCP Chatops — Host the Control Plane & Operate Conversationally

**Branch**: `007-mcp-chatops` | **Date**: 2026-06-18 | **Spec**: [spec.md](spec.md)

**Input**: Feature specification from `/specs/007-mcp-chatops/spec.md`

## Summary

Stand the spec-006 control plane up as a **real, in-Azure service** and add a **conversational front-end**.
Two coupled deliverables:

1. **Production hosting** (the spec-006 deferrals). Host the existing `Pdp.ControlPlane.Api` (webhook
   handler + Wolverine durable inbox/outbox + polling reconciler) and `Pdp.ControlPlane.Ingress` (YARP
   reverse proxy) on **Azure Container Apps** (workload-profiles environment, **VNet-integrated into the
   existing control-plane VNet** so they reach the **private IPAM-ledger / registry Postgres** the laptop
   cannot). All Azure access uses **per-app user-assigned managed identities** (Entra-token auth to
   Postgres, Resource Graph reads, ACR pull, Key Vault secret read) — **zero stored cloud secrets**; the
   **pdp-orchestrator GitHub App private key (+ webhook HMAC secret)** live in **Key Vault**, pulled via
   UAMI, and remain the only non-Azure secret. Provision **Application Insights (+ Log Analytics)** to
   receive the `env_id`-correlated telemetry the control plane already emits (spec-006 FR-O1).

2. **The `pdp-mcp` chatops server.** A new ASP.NET Core **MCP server** (MCP C# SDK, **stateless streamable
   HTTP**) that **hosts the spec-006 verb layer in-process — exactly as the `pdp` CLI does** (direct
   `ProjectReference` to `Pdp.ControlPlane.Verbs`, no reimplementation) — and exposes the verbs as MCP
   tools. It is gated as an **OAuth 2.1 protected resource** (Entra JWT validated at the server; single
   allow-listed owner `oid`). The Article VIII gate is surfaced as a **two-tool plan→confirm pattern**:
   a destroy tool refuses to act without a confirmation token **and** a verbatim restatement of the target.

**Public surface = one app.** Per the 2026-06-18 clarification, the **YARP ingress is the single
external-ingress app**; it routes `/webhooks/github` → the **internal** Api and `/mcp` (+
`/.well-known/oauth-protected-resource`) → the **internal** MCP server. Everything else is internal —
a *tighter* Article IX posture than two public endpoints.

**Footprint is isolated and destroyable.** All new Azure resources live in a **new adjacent OpenTofu
stack `infra/control-plane-host`** (own RG + state), **consuming** the existing VNet / Postgres / DNS by
reference, so a dispatched `tofu destroy` over the host stack leaves zero residual footprint and never
touches the `prevent_destroy` + `CanNotDelete`-protected ledger RG. This **unblocks the spec-006 live
acceptance (T071/T072)**: the control plane now executes in-VNet, next to the ledger.

**No new platform capability or verb** — hosting + a thin MCP adapter only. No APIM, no multi-user authz,
no workload archetypes (spec 8), no second region (spec 9).

## Amendment 2026-06-24 — async, non-blocking chat plan→confirm→apply (US2)

A defect surfaced in US2's authored-but-not-yet-live MCP plan→apply flow: the `Plan*` tools returned at
plan **dispatch** (per the original `mcp-tool-surface.md` pseudocode — `EnsureOwner; verb.Plan; issue
token`), so the paired `Apply*` raced ahead of the plan run being recorded `Succeeded` and was rejected by
the verb-layer single-flight guard (FR-022a), while the owner "confirmed" a plan they never saw. The CLI
avoids this by polling the plan to terminal in-process before showing/applying; the MCP tools skipped that.

**Resolution** (spec Clarifications 2026-06-24; FR-018/FR-019/FR-020, SC-012):
- **Both `Plan*` and `Apply*`/`Destroy*` are dispatch-and-return** — **no** MCP tool call blocks on a
  workflow (matching `Apply*`'s already-existing posture).
- **Completion is discovered by on-demand reconcile in the status-read tools only** (`ShowEnvironment` /
  `RunStatus` call `IRunTracker.ReconcileInFlightAsync` before reading — correlate by run-name, poll the
  execution plane, idempotent first-terminal-wins). `Plan*`/`Apply*`/`Destroy*` never reconcile. This
  reuses spec-006 run-tracking — **no new verb** — and makes the chat surface self-sufficient when the
  always-on Api node's reconciler/webhook is unavailable (research §13 amendment).
- **`Apply*`/`Destroy*` is a pure registry read + dispatch**: it proceeds only when the plan run is recorded
  `Succeeded`; otherwise it returns a **distinct, retryable "plan not ready" / "plan failed"** response,
  kept separate from the genuine single-flight rejection.
- **Confirmation token**: TTL widened to **~15 min** (issued at dispatch ⇒ must cover plan queue + run +
  review) and **consumed only on a successful gated dispatch** (preserved on "plan not ready" / "plan
  failed" / mismatch) so a slow or failed plan never burns it.

**Artifacts updated**: `spec.md` (US2 scenarios, edge cases, FR-018/019/020, SC-012, Clarifications
2026-06-24), `contracts/mcp-tool-surface.md` (async gate + reconcile-only-in-status-reads invariant),
`research.md` §12/§13 (timing/TTL/preservation; on-demand reconcile), `data-model.md` §5 (TTL + token
consumption). `/speckit-tasks` will correct the T032 "no in-tool polling" note and add the US2 tasks; the
already-pending live steps T040/T041 are the acceptance for this fix.

**Constitution re-check (amendment)**: **PASS, no new deviations.** No new dependency, **no new Azure
resource**, **no new platform verb** (Article II/X) — the change is adapter sequencing + reuse of the
spec-006 reconcile path. Article VIII is **strengthened** (the owner now reviews the *actual* `tofu plan`
before confirming, instead of an empty placeholder). No change to the control/execution-plane split,
division of truth, or teardown scope. Complexity Tracking remains empty.

## Technical Context

**Language/Version**: .NET 10 (LTS), C# `latest` — pinned via `global.json` / `Directory.Build.props`
(`net10.0`, nullable on, analyzers + warnings-as-errors). No Python. IaC = **OpenTofu 1.11.x** only.

**Primary Dependencies** (NuGet pinned in `docs/tech-stack.md` / `Directory.Packages.props`; surfaces
verified live via MCP — see `research.md`):
- **`ModelContextProtocol.AspNetCore`** (MCP C# SDK, pre-1.0; pulls in `ModelContextProtocol`) —
  `AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<…>()` + `MapMcp().RequireAuthorization(…)`.
- **`Microsoft.AspNetCore.Authentication.JwtBearer`** + the SDK's `.AddMcp(…)` PRM publisher — Entra v2.0
  JWT validation + the `/.well-known/oauth-protected-resource` discovery the MCP auth spec requires.
  (`Microsoft.Identity.Web` optional; explicit two-scheme `McpAuthenticationDefaults` + `AddJwtBearer`.)
- **`Pdp.ControlPlane.Verbs`** (spec 006, in-repo) — referenced **directly**; the MCP server hosts it
  in-process exactly as `Pdp.Cli` does. **No reimplementation** (FR-010, SC-003).
- **`Pdp.ControlPlane.Api` / `.Ingress` / `.Inventory` / `.Ipam` / `.Registry` / `.Dispatch`** (spec 006)
  — hosted/consumed unchanged; YARP config extended with the MCP route.
- **`Azure.Identity`** — `ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(...))` for
  the pinned per-app UAMI (reuses the spec-005/006 pluggable `TokenCredential` seam).
- Npgsql **`UsePeriodicPasswordProvider`** + `Azure.Identity` — UAMI Entra-token auth to Postgres (token
  scope `https://ossrdbms-aad.database.windows.net/.default`). No dedicated package (the assumed
  `Microsoft.Azure.PostgreSQL.Auth` does not exist on NuGet — research §5).
- **OpenTofu + AVM (Terraform flavor)**: `Azure/avm-res-app-managedenvironment`, `…-res-app-containerapp`,
  `…-res-containerregistry-registry`, `…-res-operationalinsights-workspace`, `…-res-insights-component`,
  `…-res-keyvault-vault`; `azurerm` provider for `azurerm_user_assigned_identity` + `azurerm_role_assignment`
  (no AVM UAMI module). `azurerm` 4.x, `azapi` 2.x (AVM ACA modules wrap `azapi`).
- **No prohibited deps**: no MediatR / MassTransit / AutoMapper / Moq / Serilog / FluentAssertions v8+.

**Storage**: **no new database, no new schema** — the hosted control plane reaches the **existing**
platform Postgres (the spec-002 IPAM-ledger flexible server) holding the `ipam`, `registry`, and
`wolverine` schemas (spec 006). This spec adds **Azure infrastructure**, not data: ACA, ACR, Log
Analytics, Application Insights, Key Vault, and per-app UAMIs.

**Testing**: xUnit + Shouldly + NSubstitute. **`Microsoft.AspNetCore.Mvc.Testing`**
(`WebApplicationFactory`) for the MCP endpoint: 401 + PRM challenge when unauthenticated, 200 for the
owner `oid`, **rejection for a non-owner `oid`**, and tool→verb dispatch (verb interfaces substituted with
NSubstitute). Confirmation-token unit tests (plan→confirm, verbatim target restatement, single-use +
expiry). The verb-layer logic itself is already covered by spec-006's Testcontainers/WireMock suites and
is **not** re-tested here. Live proof = the quickstart against `westus3` (incl. spec-006 T071/T072).

**Target Platform**: **Azure Container Apps** (workload-profiles environment, VNet-integrated), three apps
— `ingress` (external ingress, always-on), `api` (internal ingress, always-on), `mcp` (internal ingress,
scale-to-zero). Reached by Claude as a remote MCP client over the public YARP `/mcp` route.

**Project Type**: .NET solution addition (one new host project `Pdp.Mcp` + its tests) wired into `Pdp.sln`,
**plus** Dockerfiles for the three hosted apps, an extension to the YARP ingress config, a **new OpenTofu
stack `infra/control-plane-host`**, a one-line subnet edit to `infra/control-plane`, and CI workflows
(image build/push over OIDC; host plan/apply on the existing rails; host destroy).

**Performance Goals**: inherit spec-006 SC-006 (reconciler ≤60s sweep, missed-webhook terminal ~2 min) and
SC-012 (dispatch acknowledged P95 <5 s). MCP cold-start (scale-from-zero) latency on the first call after
idle is acceptable for an owner-only server (stateless transport ⇒ any replica serves any call).

**Constraints**: Article I (all Azure mutations via OpenTofu dispatched in CI; portal/`az` mutations
forbidden) · Article IV (isolated host-stack teardown; ledger RG never in scope) · Article V (AVM-first;
README justification for the two `azurerm` UAMI/role resources) · Article VIII (plan→confirm surfaced
through chat tools; unbypassable destroy confirm) · Article IX (one public app; least-privilege per-app
UAMI; **no standing cloud write credential**; GitHub App key the only non-Azure secret, in Key Vault) ·
Article VI (ACA subnet carved from the seeded `10.0.0.0/24` reservation; no invented range).

**Scale/Scope**: single-owner; three container apps; a handful of images; tens of spokes / hundreds of
runs over time. Cheapest viable: ACA Consumption profile, ACR Basic, Log Analytics PerGB2018 w/ daily cap,
workspace-based App Insights, Standard Key Vault.

## Constitution Check

*GATE: evaluated before Phase 0; re-checked after Phase 1 design. Result: **PASS** — this spec completes
the spec-006 hosting deferrals and adds a thin, gated MCP adapter; the single public ingress is the
Article IX-sanctioned exception, now consolidated to **one** app. No deviation requires justification.*

| Article | Gate | Status |
|---|---|---|
| I — Infra is Code | All new Azure resources are AVM/OpenTofu in the new `infra/control-plane-host` stack, dispatched via the existing CI rails (plan on PR / apply on merge); no portal/`az` mutation; CI pushes images to ACR over OIDC. | ✅ |
| II — AI calls verbs / plane split | The MCP server **hosts the verb layer in-process** and exposes verbs as tools — **no reimplementation, no new verb**; it dispatches to the execution plane exactly as the CLI does; **never runs `tofu` in-process**. | ✅ (central) |
| III — Tagged/tracked, ARG-sourced | "What's deployed?" stays the spec-005 inventory (ARG); registry/audit answer "what I asked / what happened"; division of truth unchanged. New RG carries the `pdp-*` tag schema. | ✅ |
| IV — Destroyable | All spec-7 resources in an **isolated stack** (own RG + state), consuming the ledger by reference; a dispatched `tofu destroy` removes them and **never** touches the `prevent_destroy`+`CanNotDelete` ledger RG. Teardown is acceptance (SC-010); per-app UAMI grants + the one manual Postgres-principal step are cleaned up. | ✅ |
| V — AVM-first | ACA env, container apps, ACR, Log Analytics, App Insights, Key Vault all via published AVM modules (research §1). Only `azurerm_user_assigned_identity` + `azurerm_role_assignment` are provider resources (no AVM UAMI module) — **justified in the stack README**. | ✅ |
| VI — No address without allocation | The ACA subnet (`10.0.0.32/27`) is carved from the **seeded** `10.0.0.0/24` control-plane reservation (declared by the VNet-owning stack), not an invented range; no new supernet. | ✅ |
| VII — Hub owns egress | Unchanged; the control-plane VNet/ACA alter no spoke routing. | ✅ N/A |
| VIII — Plan before apply / confirm destroy | Mutating tools surface the verb-layer **plan**; destroy tools require a **confirmation token + verbatim target restatement** (two-tool pattern), unbypassable from chat. | ✅ (central) |
| IX — Secure & cheap | **One** public app (YARP ingress, two routes) — internal Api/MCP/Postgres; per-app least-privilege UAMI; **no standing cloud write credential** (writes only in OIDC CI); GitHub App key (+ webhook secret) in Key Vault via UAMI = the only non-Azure secret; smallest SKUs; MCP scale-to-zero. | ✅ |
| X — Specs before code | specify → clarify (5 Q) → plan → tasks → implement, followed; glossary terms (`control plane`, `execution plane`, `chatops`, `env_id`) already defined. | ✅ |

**Additional constraints**: .NET 10 pinned ✅ · no prohibited deps ✅ · control vs execution plane
preserved (MCP/CLI dispatch; OpenTofu runs only in CI via OIDC) ✅ · division of truth honored ✅ ·
`.NET` naming: new host is **`Pdp.Mcp`** (assembly `pdp-mcp`), mirroring `Pdp.Cli` ✅ · **no new glossary
term required** (chatops/control-plane/execution-plane/env_id already canonical) ✅.

**Post-Phase-1 re-check**: the design artifacts introduce **one** public ingress app (fewer than the
spec's original two endpoints), no in-process IaC, no second source of deployment truth, and an isolated
destroyable stack. **Still PASS** — Complexity Tracking remains empty.

## Project Structure

### Documentation (this feature)

```text
specs/007-mcp-chatops/
├── plan.md              # This file
├── research.md          # Phase 0 — 13 decisions (ACA topology, subnet, DNS, ingress, UAMI→Postgres,
│                        #   ACR pull, Key Vault secret delivery, MCP SDK transport/tools/auth,
│                        #   plan/confirm two-tool, cross-stack subnet, SKUs/cost, principal bootstrap,
│                        #   §13 in-process verb hosting + reconciler pinned to the Api node)
├── data-model.md        # Phase 1 — Azure resource & identity model, MCP tool↔verb map, auth principals
│                        #   (NO new DB entities — reuses the spec-006 registry/ipam schemas)
├── quickstart.md        # Phase 1 — live validation: deploy host stack → MCP vend/destroy w/ gate →
│                        #   telemetry by env_id → spec-006 T071/T072 → isolated teardown
├── contracts/
│   ├── hosting-topology.md      # ACA env + 3 apps, ingress routing, VNet/DNS, the infra/control-plane-host stack
│   ├── identity-and-auth.md     # per-app UAMI grants, Postgres principal, Key Vault, MCP OAuth 2.1 PRM + oid allow-list
│   └── mcp-tool-surface.md      # the MCP tools, 1:1 verb mapping, two-tool plan/confirm shape
├── checklists/requirements.md   # (from /speckit-specify; re-validated by /speckit-clarify)
└── tasks.md             # Phase 2 — /speckit-tasks (NOT created here)
```

### Source Code (repository root)

```text
src/Pdp.Mcp/                            # NEW ASP.NET Core MCP server (assembly `pdp-mcp`) — thin adapter
├── Program.cs                          #   AddMcpServer().WithHttpTransport(Stateless=true).WithTools<…>();
│                                       #   AddJwtBearer(Entra v2.0) + .AddMcp(PRM); MapMcp().RequireAuthorization(OwnerOnly)
├── Tools/{Spoke,Fabric,Ipam,Inventory,Run}Tools.cs  # [McpServerToolType]; ctor-inject I*Verbs (spec 006); 1:1 verb calls
├── Auth/OwnerAuthorization.cs          #   "OwnerOnly" policy: RequireClaim("oid", ownerOid); MapInboundClaims=false
├── Confirm/{IConfirmationTokens,ConfirmationTokenService}.cs  # signed, single-use, time-limited {op,target} tokens
├── appsettings.json                    #   AzureAd (tenant/audience), OwnerOid, KeyVault uri — values via env/KV at runtime
├── Dockerfile                          #   multi-stage net10.0 publish
└── README.md                           #   "this is pdp-mcp (spec 007); it hosts the verb layer in-process like the CLI"

src/Pdp.ControlPlane.Api/Dockerfile     # NEW — containerize the spec-006 Api (webhook handler + reconciler)
src/Pdp.ControlPlane.Ingress/Dockerfile # NEW — containerize the spec-006 YARP ingress
src/Pdp.ControlPlane.Ingress/appsettings.json  # EDIT — add YARP routes: /webhooks/github→internal api,
                                        #   /mcp + /.well-known/oauth-protected-resource→internal mcp (streamable HTTP passthrough)

tests/Pdp.Mcp.Tests/                    # NEW — NSubstitute (verb interfaces) + Shouldly + WebApplicationFactory
├── auth: 401+PRM challenge unauthenticated, 200 owner oid, reject non-owner oid (SC-006)
├── tools: each tool calls its verb 1:1 (no logic); structured result passthrough
└── confirm: plan→confirm flow, verbatim target restatement required, single-use + expiry (Article VIII)

infra/control-plane-host/               # NEW OpenTofu stack — own RG + state platform/control-plane-host
├── backend.tf                          #   azurerm backend key = platform/control-plane-host
├── main.tf                             #   ACA env (workload profiles, VNet-integrated, external) + 3 container apps
│                                       #   + 3 UAMIs + role_assignments + ACR(Basic) + LogAnalytics + AppInsights + KeyVault
├── data.tf                             #   data sources: ACA subnet, Postgres FQDN, platform-dns RG, GitHub App secrets→KV
├── variables.tf / outputs.tf / versions.tf
└── README.md                           #   AVM module list + the two azurerm UAMI/role justifications (Article V);
                                        #   the one-time Postgres-principal bootstrap psql (research §; SC-010 manual step)

infra/control-plane/main.tf             # EDIT — add ACA subnet `snet-pdp-westus3-aca` 10.0.0.32/27
                                        #   (delegation Microsoft.App/environments) to the VNet module's subnets map
infra/control-plane/outputs.tf          # EDIT — output the ACA subnet id for the host stack data source

.github/workflows/controlplane-host-images.yml   # NEW — build+push api/ingress/mcp images to ACR over OIDC
.github/workflows/controlplane-host-destroy.yml   # NEW — gated tofu destroy over infra/control-plane-host (Article VIII)
.github/workflows/iac-plan.yml / iac-apply.yml    # (existing rails pick up infra/control-plane-host on PR/merge)

Pdp.sln                                 # EDIT — add src/Pdp.Mcp + tests/Pdp.Mcp.Tests
docs/tech-stack.md                      # EDIT — record the MCP SDK + JwtBearer pins (Postgres auth = Npgsql provider, no pkg)
```

**Structure Decision**: The MCP server is one new thin host (`Pdp.Mcp`) that **reuses the spec-006 verb
layer by direct project reference** — the same pattern `Pdp.Cli` uses — so there is **one** verb
implementation behind CLI and MCP (SC-003/SC-009). The Article VIII gate stays in the verb layer; the MCP
adapter only **surfaces** it as a two-tool plan→confirm pair. All hosting concerns are pushed into the
**isolated `infra/control-plane-host` OpenTofu stack** so the spec's footprint is destroyable independent
of the protected ledger; the only edit to the existing `infra/control-plane` stack is declaring the ACA
subnet inside its VNet-owning module (avoiding AVM VNet-module subnet drift — research §10). The YARP
ingress gains one route group (the MCP path) and stays logic-free, preserving spec-006's reverse-proxy →
internal-handler topology and consolidating to a **single public app**.

## Complexity Tracking

> No constitution violations. The single public ingress is the Article IX-sanctioned exception the spec
> demands — now **one** app (two routes) rather than two endpoints, a tighter posture. The two `azurerm`
> UAMI/role resources are not an AVM violation (no AVM UAMI module exists); their justification is recorded
> in the stack README per Article V. The table is intentionally empty.

| Violation | Why Needed | Simpler Alternative Rejected Because |
|-----------|------------|--------------------------------------|
| _(none)_ | — | — |
