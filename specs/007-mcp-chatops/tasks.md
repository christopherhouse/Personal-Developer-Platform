---
description: "Task list for MCP Chatops — Host the Control Plane & Operate Conversationally (spec 007)"
---

# Tasks: MCP Chatops — Host the Control Plane & Operate Conversationally

**Input**: Design documents from `/specs/007-mcp-chatops/`

**Prerequisites**: plan.md ✅, spec.md ✅, research.md ✅, data-model.md ✅, contracts/ ✅

**Tests**: INCLUDED for the **MCP host** (`Pdp.Mcp`) — plan §Testing requires `WebApplicationFactory`
auth tests, tool→verb adapter tests, and confirmation-token tests. The spec-006 verb layer is already
tested (Testcontainers/WireMock) and is **not** re-tested here. The OpenTofu stack is validated by
`tofu fmt`/`validate` + the live `quickstart.md` (no unit-test framework for HCL in this repo).

**Organization**: by user story (US1–US5 from spec.md), priority order. **MVP = US1 + US2** (both P1 —
US1 hosts the control plane in-VNet and unblocks T071/T072; US2 adds the conversational vend/destroy with
the Article VIII gate).

## Format: `[ID] [P?] [Story] Description`

- **[P]**: parallelizable (different files, no incomplete-task dependency)
- **[Story]**: US1–US5 for story-phase tasks; Setup/Foundational/Polish carry no story label
- Exact file paths included

## Path conventions

New .NET project `src/Pdp.Mcp/` (+ `tests/Pdp.Mcp.Tests/`) wired into `Pdp.sln`; new OpenTofu stack
`infra/control-plane-host/`; one edit to the VNet-owning `infra/control-plane/`; workflows under
`.github/workflows/`. Reuses the spec-006 `Pdp.ControlPlane.*` projects unchanged (except the YARP
ingress config + Dockerfiles).

---

## Phase 1: Setup (Shared Infrastructure)

**Purpose**: scaffold the new .NET project, the host OpenTofu stack, and containerization.

- [X] T001 [P] Create ASP.NET Core project `src/Pdp.Mcp/` (net10.0, assembly name `pdp-mcp`) and add to `Pdp.sln`
- [X] T002 [P] Create test project `tests/Pdp.Mcp.Tests/` (xUnit) and add to `Pdp.sln`
- [X] T003 Add NuGet pins to `Directory.Packages.props`: `ModelContextProtocol.AspNetCore` (0.9.0-preview.2), `Microsoft.AspNetCore.Authentication.JwtBearer` (10.0.9); `Microsoft.AspNetCore.Mvc.Testing` already pinned (spec 006) — **no prohibited deps**. NOTE: `Microsoft.Azure.PostgreSQL.Auth` does **not** exist on NuGet (research-agent error); UAMI→Postgres Entra-token auth uses Npgsql `UsePeriodicPasswordProvider` + `Azure.Identity` (research §5 corrected) — no extra package, wired in T012
- [X] T004 Wire project references (`Pdp.Mcp` → `Pdp.ControlPlane.Verbs`; `Pdp.Mcp.Tests` → `Pdp.Mcp` + `Pdp.ControlPlane.TestSupport`) and confirm `dotnet build` succeeds — full solution green (0 warnings, 0 errors)
- [X] T005 [P] Create OpenTofu stack skeleton `infra/control-plane-host/` — `backend.tf` (key `platform/control-plane-host`), `versions.tf` (pin OpenTofu 1.11.x, `azurerm` 4.x, `azapi` 2.x), `variables.tf`, `main.tf` (locals), `outputs.tf`, `data.tf`, `README.md` — `tofu init -backend=false` + `validate` succeed, `fmt` clean
- [X] T006 [P] Add multi-stage `Dockerfile` (net10.0 publish, context = repo root) to `src/Pdp.ControlPlane.Api/`, `src/Pdp.ControlPlane.Ingress/`, and `src/Pdp.Mcp/`; add root `.dockerignore`
- [X] T007 [P] Add `README.md` to `src/Pdp.Mcp/` ("pdp-mcp, spec 007; hosts the verb layer in-process like the CLI; thin adapter, no reimplementation")

**Checkpoint**: ✅ solution builds with the scaffolded MCP project (reusing the verb layer by reference); the host stack skeleton `tofu init`s + `validate`s.

---

## Phase 2: Foundational (Blocking Prerequisites)

**Purpose**: shared prerequisites for the infra track (subnet, AVM validation, data sources) and the app
track (MCP host bootstrap + auth). **No user story may start until this phase is complete.**

- [X] T008 Edit `infra/control-plane/main.tf`: add subnet `snet-pdp-westus3-aca` `10.0.0.32/27` (delegation `Microsoft.App/environments`) to the AVM VNet module's `subnets` map; add its id to `infra/control-plane/outputs.tf` (research §10 — avoids AVM VNet-module subnet drift) — `tofu fmt`/`validate` green
- [X] T009 Smoke-validate + pin exact versions of the AVM modules under OpenTofu 1.11.x (Article V): `avm-res-app-managedenvironment` 0.5.0, `avm-res-app-containerapp` 0.9.0, `avm-res-containerregistry-registry` 0.5.1, `avm-res-operationalinsights-workspace` 0.5.1, `avm-res-insights-component` 0.4.0, `avm-res-keyvault-vault` 0.10.2; pins + the two `azurerm` UAMI/role justifications recorded in `infra/control-plane-host/README.md` (per-block `tofu init` smoke-validation runs as each module block lands in US1/US2/US4, per the README)
- [X] T010 Implement `infra/control-plane-host/data.tf`: data sources for the ACA subnet (by name + VNet), the Postgres server/FQDN, and the platform-dns RG; `tofu validate` passes
- [X] T011 Implement `src/Pdp.Mcp/Program.cs` base bootstrap: `AddMcpServer().WithHttpTransport(o => o.Stateless = true)`; `AddControlPlaneVerbs(...)` (DI the spec-006 verb layer in-process) with `ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(...))`; App Insights surfaced from `APPLICATIONINSIGHTS_CONNECTION_STRING` into the verb-layer telemetry (`UseAzureMonitor`, graceful if empty); **Wolverine `DurabilityMode.Serverless` + no `ReconcilerScheduler` on this node** (research §13 — the Api owns the reconciler)
- [X] T012 Implement Postgres Entra-token auth wiring in `src/Pdp.Mcp/` (and shared seam `Pdp.ControlPlane.Verbs/Hosting/EntraPostgres.cs`): Npgsql `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider` calling `Azure.Identity` `GetTokenAsync` (scope `https://ossrdbms-aad.database.windows.net/.default`, ~55-min refresh; Username = UAMI display name) — **no** `Microsoft.Azure.PostgreSQL.Auth` package (does not exist, research §5); `AddControlPlaneVerbs`/`ConfigureControlPlaneMessaging` take an optional data source (CLI/tests unchanged)
- [X] T013 Implement `src/Pdp.Mcp/Auth/OwnerAuthorization.cs`: JWT bearer (Entra v2.0 authority, explicit `ValidAudience`/`ValidIssuer`/`ValidateLifetime`, `MapInboundClaims = false`) + `.AddMcp(o => o.ResourceMetadata = ...)` PRM + `"OwnerOnly"` policy `RequireClaim("oid", ownerOid)`; `app.MapMcp().RequireAuthorization("OwnerOnly")` (contracts/identity-and-auth.md)

**Checkpoint**: the MCP host starts with auth wired (no tools yet); the host stack validates against the live VNet/Postgres data sources.

---

## Phase 3: User Story 1 — Control plane runs in-Azure, in-VNet, reaching the private ledger (Priority: P1) 🎯 MVP

**Goal**: host `Pdp.ControlPlane.Api` + `Pdp.ControlPlane.Ingress` on ACA, VNet-integrated, reaching the
private Postgres via per-app UAMI Entra-token auth — unblocking spec-006 T071/T072.

**Independent Test**: the hosted `api` authenticates to the private Postgres with `uami-api` (Entra token,
no password) and reads the ledger/registry; spec-006 T071/T072 run live against the hosted control plane.

- [X] T014 [US1] Implement RG + tags + per-app UAMIs (`uami-pdp-westus3-{ingress,api,mcp}`) via `azurerm_user_assigned_identity` in `infra/control-plane-host/main.tf` (RG carries the `pdp-*` tag schema; **no `prevent_destroy`**)
- [X] T015 [P] [US1] Implement ACR (Basic SKU) via `avm-res-containerregistry-registry` + `AcrPull` role assignments to all three UAMIs in `infra/control-plane-host/main.tf` (research §7)
- [X] T016 [P] [US1] Implement Key Vault via `avm-res-keyvault-vault` (RBAC) holding `github-app-private-key` + `github-webhook-secret`; grant `Key Vault Secrets User` to `uami-api` (+ `uami-mcp`); seed secret values via the secure pipeline path, never tofu vars/state (research §8) — **vault + RBAC only; values seeded out-of-band, referenced by constructed versionless KV URI (the module writes `secrets_value` to state, so no secret is declared here — SC-007). Network posture set to RBAC-gated (`network_acls = null`); private-endpoint hardening is an owner decision flagged for T026.**
- [X] T017 [P] [US1] Implement Log Analytics workspace via `avm-res-operationalinsights-workspace` (`PerGB2018`, daily cap ~1 GB) in `infra/control-plane-host/main.tf` (required by the ACA env)
- [X] T018 [US1] Implement the ACA managed environment via `avm-res-app-managedenvironment` (workload-profiles, External, `infrastructure_subnet_id` = ACA subnet data source, `log_analytics_workspace.resource_id` = T017) in `infra/control-plane-host/main.tf` — **pinned to 0.4.0 (smoke finding: 0.5.0 needs OpenTofu 1.12; surface deltas recorded in README)**
- [X] T019 [US1] Implement `azurerm_role_assignment` granting `uami-api` subscription `Reader` (Azure Resource Graph reconcile reads) in `infra/control-plane-host/main.tf`
- [X] T020 [US1] Implement the `api` container app via `avm-res-app-containerapp` (**internal** ingress, min replicas **1**, `uami-api`, ACR image via UAMI, KV-backed secrets for GitHub App key + webhook secret, Postgres FQDN config) in `infra/control-plane-host/main.tf`
- [X] T021 [US1] Implement the `ingress` (YARP) container app via `avm-res-app-containerapp` (**external** ingress, min replicas **1**, `uami-ingress`, ACR image) and edit `src/Pdp.ControlPlane.Ingress/appsettings.json` to route `POST /webhooks/github` → internal `api` — **webhook route already present (spec 006); production destination injected via the container-app env var (`ReverseProxy__…__Address` = internal api `fqdn_url`). `/mcp` route deferred to T037 (US2).**
- [X] T022 [US1] Add `env_id`/Postgres-token config + verify the hosted `Api` reconciler/webhook handler run (always-on) against the live ledger; wire `ManagedIdentityCredential` client id for `uami-api` — **Api `Program.cs` now mirrors the Mcp UAMI→EntraPostgres token path (keeps the reconciler/scheduled agents — the sole tracking node, research §13) + surfaces App Insights conn string. Live ledger verification is part of T026.**
- [X] T023 [US1] Implement `outputs.tf` (ACR login server, app FQDNs, UAMI names/object-ids/client-ids) + emit the **one-time `pgaadauth_create_principal_with_oid` psql command** for `uami-api` as a tofu output (research §6)
- [X] T024 [US1] Author `.github/workflows/controlplane-host-images.yml`: build + push `api`/`ingress` (and `mcp`) images to ACR over **OIDC** (no registry secret)
- [X] T025 [US1] Run the one-time bootstrap for `uami-api`: `pgaadauth_create_principal_with_oid(...)` + least-privilege `GRANT` on `ipam`+`registry` (owner/Entra-admin; documented in `infra/control-plane-host/README.md`). **DONE (live, ~2026-06-2x)**: the principal was bootstrapped and the hosted apps authenticate to the private ledger — evidenced by PR #31 (`make the Postgres principal bootstrap idempotent — self-heal 28P01`, only hit by running it live), #30 (`commit app-reg bootstrap`), and #38 (`grant uami-api CREATE on database so Wolverine provisions its schema`). Manual fallback (the `pgaadauth_bootstrap_uami_api` tofu output) remains documented.
- [X] T026 [US1] Deploy via the CI rails (plan-on-PR / apply-on-merge for `infra/control-plane` subnet then `infra/control-plane-host`) and verify the hosted `api` reaches the private ledger (Entra token, no password) — quickstart Scenarios 0–1. **DONE (live)**: the host stack is deployed and the hosted control plane reaches the private ledger — evidenced by the live bring-up series PR #29 (`live data-layer bring-up — ipam schema, wolverine provisioning, pdp database`) through #38/#39 (Wolverine schema provisioning, startup-race + GitHub App dispatch fixes), all of which presuppose a running, ledger-connected deployment. Rails wired: `control-plane-host` in `iac-plan.yml` + the Phase 3 `apply-host` job in `iac-apply.yml`.
- [ ] T027 [US1] Run spec-006 quickstart **T071/T072** against the hosted control plane (live spoke vend/destroy on `westus3`, tracked to terminal) — quickstart Scenario 7 — **UNBLOCKED on hosting (T025/T026 done), but BLOCKED by the plan→apply defect this branch fixes**: a live vend/destroy currently fails at apply with the FR-022a "operation in progress" single-flight rejection because the MCP plan run isn't recorded `Succeeded` before apply (the bug Phase 9 / T062–T072 resolves). Re-run once Phase 9 lands; shares acceptance with T040/T041.

**Checkpoint**: the control plane runs in-Azure, reaches the private ledger via UAMI, and the spec-006 live acceptance is unblocked and passing.

---

## Phase 4: User Story 2 — Vend then destroy a spoke through chat, with the plan/confirm gate (Priority: P1)

**Goal**: the `pdp-mcp` server exposes vend/destroy verbs as Entra-gated MCP tools with the Article VIII
plan→confirm gate; deploy the `mcp` container app behind YARP.

**Independent Test**: from an Entra-authenticated MCP client, vend a spoke (plan surfaced before apply)
then destroy it (refused without token + verbatim target restatement) — all through chat.

### Tests for User Story 2 ⚠️ (write first; ensure they fail before implementation)

- [X] T028 [P] [US2] Auth tests in `tests/Pdp.Mcp.Tests/McpAuthTests.cs` (`WebApplicationFactory<Program>` on Testcontainers Postgres): unauthenticated `/mcp` → **401 + `WWW-Authenticate` `resource_metadata` PRM challenge**; owner `oid` → **200** (real `initialize`); **non-owner `oid` → 403** (SC-006). Real Entra tokens can't be minted in-test, so the JWT *authenticate* scheme is swapped for a header-driven `oid` handler; the `OwnerOnly` policy + MCP *challenge* scheme are the real wiring. **Found+fixed: `MapMcp()` defaulted to root — finalized to `MapMcp("/mcp")` (contract route; YARP forwards path-preserving).**
- [X] T029 [P] [US2] Confirmation-token tests in `tests/Pdp.Mcp.Tests/ConfirmationTokenServiceTests.cs`: plan→confirm flow, **verbatim target restatement required**, single-use, expiry (controllable `TimeProvider`), operation-mismatch rejected, mismatch does not consume the token (Article VIII, SC-002)
- [X] T030 [P] [US2] Tool→verb adapter tests in `tests/Pdp.Mcp.Tests/ToolVerbAdapterTests.cs` (NSubstitute `ISpokeVerbs`/`IFabricVerbs`): each tool calls its verb 1:1, no logic, structured passthrough; token-gate + owner-gate refuse before any verb runs (SC-003)

### Implementation for User Story 2

- [X] T031 [P] [US2] Implement `src/Pdp.Mcp/Confirm/IConfirmationTokens.cs` + `ConfirmationTokenService.cs`: opaque CSPRNG id, single-use, ~5-min TTL (`TimeProvider`), binds `{operation, targetName}`; throws `McpException` on missing/expired/used/mismatch (data-model §5)
- [X] T032 [US2] Implement `src/Pdp.Mcp/Tools/SpokeTools.cs` ([McpServerToolType], ctor-inject `ISpokeVerbs` + `IConfirmationTokens` + `IOptions<McpAuthOptions>`): `PlanSpokeVend`/`ApplySpokeVend`, `PlanSpokeDestroy`/`DestroySpoke` (token + verbatim name); `EnsureOwner(ClaimsPrincipal)` via shared `OwnerTool` base (contracts/mcp-tool-surface.md). Thin adapter — no in-tool polling (the contract pseudocode's `EnsureOwner; verb.Plan; issue token`); also added `Tools/McpPlanResult.cs`. **SUPERSEDED IN PART by the 2026-06-24 amendment (Phase 9, T062–T072)**: tools still never block/poll, but the async plan→apply contract changed — `Apply*` becomes a pure read+dispatch returning a distinct "plan not ready" response, on-demand reconcile moves into the `ShowEnvironment`/`RunStatus` reads, and the token gains 15-min TTL + consume-on-success. This original implementation is the source of the live plan→apply single-flight defect Phase 9 fixes.
- [X] T033 [US2] Implement `src/Pdp.Mcp/Tools/FabricTools.cs` (ctor-inject `IFabricVerbs` + `ControlPlaneOptions` for the platform-subscription natural key): `PlanFabricCreate`/`ApplyFabricCreate`, `PlanFabricDestroy`/`DestroyFabric` (token + verbatim region)
- [X] T034 [US2] Register the tool classes + `IConfirmationTokens` (singleton) in `src/Pdp.Mcp/Program.cs` (`.WithTools<SpokeTools>().WithTools<FabricTools>()`); register `McpAuthOptions` for DI in `AddOwnerAuthorization`; T028–T030 pass (23 tests green)
- [X] T035 [P] [US2] Implement the `mcp` container app via `avm-res-app-containerapp` 0.9.0 (**internal** ingress, **min replicas 0** scale-to-zero, `uami-mcp`, ACR image, KV-backed GitHub App key only, Entra-token Postgres conn, AzureAd `TenantId`/`Audience`/`OwnerOid` config) in `infra/control-plane-host/main.tf` — App Insights conn string deferred to US4/T049 (graceful no-op until then)
- [X] T036 [US2] Implement `azurerm_role_assignment` granting `uami-mcp` subscription `Reader`; add the `uami-mcp` `pgaadauth_bootstrap_uami_mcp` tofu output + `mcp_fqdn_url`; wire the ingress `control-plane-mcp` YARP destination env to `module.container_app_mcp.fqdn_url` (research §6)
- [X] T037 [US2] Edit `src/Pdp.ControlPlane.Ingress/appsettings.json`: add YARP routes `/mcp` (+ `/mcp/{**catch-all}`) and `/.well-known/oauth-protected-resource` (+ catch-all) → internal `control-plane-mcp` cluster (streamable-HTTP passthrough; YARP forwards `Authorization`, validates nothing; no business logic) (contracts/hosting-topology.md)
- [ ] T038 [US2] Run the one-time bootstrap for `uami-mcp` (`pgaadauth_create_principal_with_oid` + `GRANT` on `ipam`+`registry`) — **BLOCKED (live): the mcp half of the single reviewed manual step. Covered by the same `scripts/bootstrap-postgres-principals.{ps1,sh}` helper (run `mcp`, or default = both); manual fallback = the `pgaadauth_bootstrap_uami_mcp` tofu output.**
- [ ] T039 [US2] Deploy (images + `tofu apply`) and verify the MCP auth gate live — quickstart Scenario 2
- [ ] T040 [US2] Live: vend a spoke through an MCP client with the plan surfaced before apply, tracked to terminal — quickstart Scenario 3 — **also the acceptance for the Phase 9 amendment**: `PlanSpokeVend` returns a token immediately without blocking; a `ShowEnvironment`/`RunStatus` read surfaces the real plan once it completes; `ApplySpokeVend` before the plan finishes returns the distinct "plan not ready" message (NOT single-flight) and does NOT consume the token; the confirmed apply tracks to terminal (SC-012). Verify it works with the Api node's background reconciler disabled.
- [ ] T041 [US2] Live: destroy the spoke — refused without token + verbatim name, then succeeds (allocation released, env `destroyed`) — quickstart Scenario 4 — **also verifies the Phase 9 amendment for the destroy flow**: `PlanSpokeDestroy` returns a token immediately; `DestroySpoke` before the destroy-plan finishes returns "plan not ready" (token preserved); the confirmed destroy tracks to terminal.

**Checkpoint**: the owner can vend and destroy a spoke end-to-end through chat with the unbypassable Article VIII gate.

---

## Phase 5: User Story 3 — Conversational reads: inventory, IPAM, intent/run history (Priority: P2)

**Goal**: expose the read verbs as MCP tools — "what's deployed?" (ARG), IPAM query, intent/run history.

**Independent Test**: from the MCP client, ask the inventory / IPAM / history questions; answers are
correctly sourced (deployed ⇒ ARG; intent/history ⇒ registry) with no duplicated query logic.

### Tests for User Story 3 ⚠️

- [X] T042 [P] [US3] Read-tool tests in `tests/Pdp.Mcp.Tests/ReadToolAdapterTests.cs` (NSubstitute `IIpamVerbs`/`IInventoryVerbs`/`IRunVerbs`): each tool calls its verb 1:1; division-of-truth assertion (inventory ⇒ ARG vs registry reads) + env-ref/run-id parse-guards + owner gate (SC-003/SC-004). 10 tests, 33 total green.

### Implementation for User Story 3

- [X] T043 [P] [US3] Implement `src/Pdp.Mcp/Tools/IpamTools.cs` (ctor-inject `IIpamVerbs`, owner-gated via `OwnerTool`): `QueryIpam` — one region ⇒ `QueryAsync`, omitted ⇒ `QueryAllAsync` (mirrors `pdp ipam query`)
- [X] T044 [P] [US3] Implement `src/Pdp.Mcp/Tools/InventoryTools.cs` (ctor-inject `IInventoryVerbs`): `WhatsDeployed` (`GetSnapshotAsync`)/`ListEnvironments` (`GetEnvironmentsAsync`) — ARG side of the division of truth
- [X] T045 [P] [US3] Implement `src/Pdp.Mcp/Tools/RunTools.cs` (ctor-inject `IRunVerbs`): `ShowEnvironment` (`GetEnvironmentAsync`)/`RunHistory` (`GetRunsAsync`)/`RunStatus` (`GetRunAsync`) — registry side; env-ref + run-id parsed with `McpException` guards
- [X] T046 [US3] Register the read-tool classes in `src/Pdp.Mcp/Program.cs` (`.WithTools<IpamTools>().WithTools<InventoryTools>().WithTools<RunTools>()`); T042 passes (33 tests green)
- [ ] T047 [US3] Live: ask "what's deployed?" / IPAM / "what did I ask, what happened?" and verify correct sourcing — quickstart Scenario 5 — **BLOCKED (live): depends on the deployed `mcp` app (T039); not executable from this environment.**

**Checkpoint**: conversational reads work and preserve division of truth.

---

## Phase 6: User Story 4 — env_id-correlated telemetry in Application Insights (Priority: P2)

**Goal**: provision the App Insights sink and wire the apps to export the telemetry they already emit.

**Independent Test**: trigger a verb/run, then trace it end-to-end from a single `env_id` in App Insights.

- [X] T048 [US4] Implement Application Insights via `avm-res-insights-component` 0.4.0 (workspace-based; `workspace_id` = the US1 Log Analytics workspace `resource_id`, `application_type` = `web`) in `infra/control-plane-host/main.tf`; sensitive `application_insights` stack output added. Smoke-validated under OpenTofu 1.11.6 (`fmt`/`init -backend=false`/`validate` green); finding recorded in README.
- [X] T049 [US4] Set `APPLICATIONINSIGHTS_CONNECTION_STRING` on the `api` and `mcp` container apps from `module.application_insights.connection_string` so the verb layer's `UseAzureMonitor()` exports there. **App side already reads the key (Program.cs, T011/T022); plain env (Azure ingestion credential, not an SC-007 non-Azure secret).**
- [ ] T050 [US4] Deploy and verify `env_id`-correlated logs + traces land in App Insights (verb → dispatch → run-state transitions → terminal) — quickstart Scenario 6 — **BLOCKED (live): runs in dispatched CI against Azure (Article I); not executable from this environment.**
- [X] T051 [US4] Confirm telemetry-export failure is non-fatal (app operates if App Insights is unreachable) — spec edge case. **Satisfied by design: `ControlPlaneTelemetry.AddControlPlaneTelemetry` attaches `UseAzureMonitor` ONLY when the connection string is non-empty (empty ⇒ graceful no-op); when set-but-unreachable the Azure Monitor OpenTelemetry exporter buffers/backs-off/drops off the request path and never faults the app (spec-006 research §11, verified live). Live re-confirmation folds into T050.**

**Checkpoint**: a run is traceable end-to-end from one `env_id` in Application Insights.

---

## Phase 7: User Story 5 — The whole spec-7 footprint tears down cleanly (Priority: P3)

**Goal**: a dispatched `tofu destroy` over the host stack leaves zero residual footprint; the protected
ledger RG is untouched.

**Independent Test**: dispatch the destroy, run the manual principal cleanup, verify via Resource Graph
that nothing spec-7 remains and the ledger RG is intact.

- [X] T052 [US5] Author `.github/workflows/controlplane-host-destroy.yml`: manual `workflow_dispatch`, **gated** on a typed `control-plane-host` confirmation (Article VIII), OIDC auth, `tofu plan -destroy` (for the record) → `tofu destroy -auto-approve` over `infra/control-plane-host`, with a post-destroy step-summary reminder for the manual principal cleanup. Mirrors the spec-006 `controlplane-destroy.yml` pattern; concurrency group `tofu-control-plane-host`.
- [X] T053 [US5] Document the manual teardown cleanup in `infra/control-plane-host/README.md`: expanded the Teardown section to a 3-step runbook — dispatch the gated destroy, then (SC-010 manual step) `REASSIGN OWNED`/`DROP OWNED`/`DROP ROLE` for the `uami-api`/`uami-mcp` `pgaadauth` principals (the inverse of the bootstrap grants), then Resource-Graph zero-residue verification (subscription Reader grants delete with the UAMIs).
- [ ] T054 [US5] Verify (live) the dispatched destroy removes all spec-7 resources (ACA env+apps, 3 UAMIs, ACR, Log Analytics, App Insights, Key Vault) — quickstart Scenario 8 — **BLOCKED (live): runs in dispatched CI against Azure (Article I).**
- [ ] T055 [US5] Verify via Resource Graph that the `prevent_destroy`+`CanNotDelete` ledger RG is **untouched** and no dangling role grants / orphaned Entra registrations remain (SC-010) — **BLOCKED (live): post-destroy verification; depends on T054.**

**Checkpoint**: the spec-7 footprint is fully destroyable; the ledger is protected.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: docs, pins, and final validation across stories.

- [X] T056 [P] Record the new pins (`ModelContextProtocol.AspNetCore` 0.9.0-preview.2, `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.9 — used **instead of** `Microsoft.Identity.Web`) and the ACA/ACR/KV/App-Insights SKUs (+ AVM module versions) in `docs/tech-stack.md`; Postgres Entra-token auth = Npgsql `UsePeriodicPasswordProvider` + `Azure.Identity` (no package).
- [X] T057 [P] Finalize `infra/control-plane-host/README.md`: AVM module list + exact versions (incl. App Insights 0.4.0), the two `azurerm` UAMI/role justifications (Article V), smoke findings (T015–T021, T048), the deploy runbook (KV seeding + principal bootstrap), and the expanded teardown runbook (T053). Status footer updated to US1+US2+US4 authored / validate-green.
- [X] T058 Run `tofu fmt -check` + `tofu validate` on `infra/control-plane-host` and `infra/control-plane` (both **fmt-clean + valid** under OpenTofu 1.11.6); `dotnet build` (0 warn/0 err), `dotnet test` (**163 tests green**, incl. Pdp.Mcp.Tests 33), `dotnet format` clean (fixed a pre-existing whitespace nit in `Auth/OwnerAuthorization.cs`).
- [ ] T059 Execute the full `quickstart.md` (Scenarios 0–8) end-to-end against `westus3` — **BLOCKED (live): the live acceptance run; depends on the dispatched deploy + principal bootstrap.**
- [X] T060 Update `docs/spec-backlog.md` Status: added the spec-7 in-progress entry (authorable work complete; live-only remaining) and the explicit **spec-006 T071/T072 UNBLOCKED** note.
- [X] T061 [P] Verify **no inline secret** lands in the host-stack config (SC-007): static assertion confirmed — ACR `admin_enabled = false` (UAMI pull, no registry password); both GitHub secrets resolve via `key_vault_secret_id` + UAMI `identity` and reach containers via `secret_name` (never a literal `value`); no `secrets_value` on the KV module. Only sensitive `value =` env is the App Insights connection string (Azure ingestion credential, out of SC-007 scope). **Live plan/state output assertion runs in CI (T026/T039).**

---

## Phase 9: Amendment 2026-06-24 — async, non-blocking chat plan→confirm→apply (US2 fix)

**Goal**: fix the live plan→apply single-flight defect (spec Clarifications 2026-06-24; FR-018/FR-019/FR-020,
SC-012). The MCP `Plan*`/`Apply*` tools both become dispatch-and-return (no tool call blocks on a workflow);
completion is discovered by **on-demand reconcile in the `ShowEnvironment`/`RunStatus` reads only**; `Apply*`
is a pure registry read + dispatch that returns a **distinct, retryable "plan not ready" / "plan failed"**
response (kept separate from the genuine single-flight rejection); the confirmation token gets a **~15-min
TTL** and is **consumed only on a successful gated dispatch**. Reuses the spec-006 run-tracking/reconcile
logic — **no new verb**.

**Independent Test**: through an MCP client, plan a spoke vend (token returned immediately, no block) →
`ShowEnvironment`/`RunStatus` surfaces the real plan once complete → apply-before-plan-finishes returns
"plan not ready" with the token preserved → confirmed apply tracks to terminal — with the Api node's
background reconciler disabled (proves the chat surface self-advances).

### Tests for Phase 9 ⚠️ (write first; ensure they fail before implementation)

- [ ] T062 [P] [US2] Extend `tests/Pdp.Mcp.Tests/ConfirmationTokenServiceTests.cs`: TTL is **~15 min** (controllable `TimeProvider`); the token is **preserved** (still redeemable) after a check that does not dispatch (the "plan not ready"/"plan failed"/mismatch paths) and **consumed only** via the explicit consume-on-success call; expiry still rejects. Update the prior "removed on first validation" assertion to the new consume-on-dispatch semantics.
- [ ] T063 [P] [US2] Extend `tests/Pdp.Mcp.Tests/ToolVerbAdapterTests.cs` (NSubstitute `ISpokeVerbs`/`IFabricVerbs`/`IRunVerbs`/`IRunTracker`): `Plan*` tools dispatch-and-return without polling; `ShowEnvironment`/`RunStatus` call `IRunTracker` reconcile **before** the read; `RunHistory` and the other reads do **not** reconcile; `Apply*`/`Destroy*` do **not** reconcile and surface the distinct "plan not ready"/"plan failed" `McpException` (vs single-flight) without consuming the token.
- [ ] T064 [P] [US2] Add verb-layer tests in `tests/Pdp.ControlPlane.Verbs.Tests/PlanConfirmTests.cs` (Testcontainers): a confirm-path `CreateAsync`/`DestroyAsync` whose latest run is a Plan still **in-flight** throws the new `PlanNotReadyException`; a **failed** plan throws `PlanFailedException`; a genuine concurrent mutation still throws `OperationInProgressException` — the three are distinct.

### Implementation for Phase 9

- [ ] T065 [US2] Add a **targeted on-demand reconcile** to the tracker: `IRunTracker.ReconcileEnvironmentAsync(Guid envId, CancellationToken)` in `src/Pdp.ControlPlane.Dispatch/IRunTracker.cs` + `RunTracker.cs`, reusing the existing `QueryGitHubRunAsync` + `RecordRunStatusAsync` (idempotent first-terminal-wins) but scoped to one environment so a status read does not sweep all in-flight envs. No new verb, no duplicated correlation logic.
- [ ] T066 [US2] Distinguish "plan not ready" from single-flight in the verb layer: add `PlanNotReadyException` + `PlanFailedException` to `src/Pdp.ControlPlane.Verbs/VerbExceptions.cs`; in `src/Pdp.ControlPlane.Verbs/Handlers/SpokeVerbs.cs` + `FabricVerbs.cs`, in the confirm branch (env Provisioning/Destroying, latest run is a Plan), throw `PlanNotReadyException` when the plan run is still in-flight and `PlanFailedException` when it failed — instead of `OperationInProgressException`. `OperationInProgressException` remains **only** for a genuine concurrent mutating operation; the single-flight invariant is unchanged. (CLI already polls to terminal before applying, so it won't hit these; ensure its exception→exit mapping in `src/Pdp.Cli/` does not crash on the new types.)
- [ ] T067 [US2] Split token consumption in `src/Pdp.Mcp/Confirm/IConfirmationTokens.cs` + `ConfirmationTokenService.cs`: a **check** that validates `{operation, target, expiry}` **without removing** the token, and an explicit **consume** that removes it; widen the TTL constant from ~5 min to **~15 min**. (Mismatch/expiry still throw and never consume — preserves the existing behavior.)
- [ ] T068 [US2] Wire on-demand reconcile into the MCP status reads in `src/Pdp.Mcp/Tools/RunTools.cs` (inject `IRunTracker`): `ShowEnvironment` and `RunStatus` resolve the target `env_id` (for `RunStatus`, via the run's env) and call `ReconcileEnvironmentAsync` **before** delegating to `IRunVerbs`; `RunHistory` and the non-status reads do **not** reconcile (contract invariant). Orchestration only — no domain logic.
- [ ] T069 [US2] Update the mutate/destroy tools in `src/Pdp.Mcp/Tools/SpokeTools.cs` + `FabricTools.cs`: call `tokens.Check(...)` (no consume) → invoke the verb → `tokens.Consume(...)` **only on a successful gated dispatch**; map `PlanNotReadyException`/`PlanFailedException` to clear, retryable `McpException` messages ("plan hasn't finished yet — check its status with ShowEnvironment/RunStatus and retry" / "the plan failed; nothing to apply"); the token is **not** consumed on those paths. `Plan*` tools stay pure dispatch-and-return.
- [ ] T070 [US2] Update tool `[Description]`s in `SpokeTools`/`FabricTools` to state the **plan → check status (ShowEnvironment/RunStatus) → apply** sequence explicitly (so the agent reviews the real plan and never skips straight from plan to apply), and that `Apply*`/`Destroy*` return "plan not ready" until the plan run has succeeded — surfacing the async contract to the model.
- [ ] T071 [US2] Run `dotnet build` (0 warn/0 err), `dotnet test` (Pdp.Mcp.Tests + Pdp.ControlPlane.Verbs.Tests green incl. T062–T064), `dotnet format` clean. Confirm no prohibited deps introduced and the verb-layer single-flight tests (spec-006) still pass unchanged.
- [ ] T072 [US2] Update the design-artifact cross-references already amended (`spec.md` FR-018/019/020 + SC-012, `contracts/mcp-tool-surface.md`, `research.md` §12/§13, `data-model.md` §5) are reflected in code comments/READMEs where the old "~5-min TTL / removed on first validation / no in-tool polling" wording appears (`src/Pdp.Mcp/README.md`, XML docs on the tools + token service); no stale contradiction remains. Live verification rides on **T040/T041**.

**Checkpoint**: the owner can plan→review→apply (and destroy) a spoke/fabric through chat with no blocking tool call, the real plan surfaced for review, a clear "plan not ready" instead of a spurious single-flight rejection, and a token that survives a premature apply — the live plan→apply defect is closed (T040/T041).

---

## Dependencies & Execution Order

### Phase dependencies

- **Setup (Phase 1)**: no dependencies.
- **Foundational (Phase 2)**: depends on Setup — **blocks all user stories** (subnet + data sources block infra; MCP host bootstrap + auth block US2/US3).
- **US1 (Phase 3)**: depends on Foundational. The hosting MVP; unblocks T071/T072.
- **US2 (Phase 4)**: depends on Foundational; its **live** steps (T039–T041) depend on US1 (deployed env + Key Vault + ACR + ingress). Unit tests (T028–T030) need only the MCP project.
- **US3 (Phase 5)**: depends on the US2 MCP host bootstrap (tool registration in the same `Program.cs`); live read (T047) depends on the deployed `mcp` app.
- **US4 (Phase 6)**: depends on US1 (Log Analytics) and the running `api`/`mcp` apps.
- **US5 (Phase 7)**: depends on the host stack existing (US1+).
- **Polish (Phase 8)**: after the desired stories are complete.
- **Amendment (Phase 9)**: depends on the US2 MCP host (Phase 4) existing — it edits `SpokeTools`/`FabricTools`/`RunTools`/`ConfirmationTokenService` and the shared verb layer + dispatch tracker. Independent of the infra/live phases; its live acceptance folds into T040/T041. Tests (T062–T064) before implementation (T065–T072); T065 (tracker method) before T068 (RunTools wiring); T066 (verb exceptions) before T069 (tool mapping); T067 (token split) before T069.

### User story dependencies

- **US1 (P1)**: independent — the hosting foundation; deliverable on its own (T071/T072 unblocked).
- **US2 (P1)**: MCP unit work is independent; live demo builds on US1's deployed hosting.
- **US3 (P2)**: extends the US2 MCP host (shared `Program.cs`); reads are additive.
- **US4 (P2)**: additive telemetry sink on US1's workspace.
- **US5 (P3)**: teardown of the US1+ footprint.

### Within each story

- Tests (US2/US3) written first and failing before implementation.
- Identities/registry/secret resources before the apps that consume them (T014–T017 before T018–T021).
- Tool classes before their registration in `Program.cs`.

### Parallel opportunities

- Setup: T001/T002/T005/T006/T007 in parallel.
- US1: T015/T016/T017 (ACR / Key Vault / Log Analytics) in parallel after T014 (UAMIs).
- US2 tests: T028/T029/T030 in parallel; T031 in parallel with the test authoring.
- US3 tools: T043/T044/T045 in parallel before T046.

---

## Parallel Example: User Story 2

```bash
# Tests first (parallel):
Task: "Auth tests (401/PRM, owner 200, non-owner reject) in tests/Pdp.Mcp.Tests/"
Task: "Confirmation-token tests (verbatim target, single-use, expiry) in tests/Pdp.Mcp.Tests/"
Task: "Tool→verb adapter tests (NSubstitute) in tests/Pdp.Mcp.Tests/"

# Then implementation:
Task: "ConfirmationTokenService in src/Pdp.Mcp/Confirm/"
Task: "SpokeTools in src/Pdp.Mcp/Tools/SpokeTools.cs"
Task: "FabricTools in src/Pdp.Mcp/Tools/FabricTools.cs"
```

---

## Implementation Strategy

### MVP first (US1 + US2)

1. Phase 1 Setup → Phase 2 Foundational.
2. Phase 3 (US1): host the control plane in-VNet; **validate T071/T072**.
3. Phase 4 (US2): MCP vend/destroy with the Article VIII gate; **validate live chat vend→destroy**.
4. STOP and demo: the owner operates the platform conversationally end-to-end.

### Incremental delivery

US1 (hosting + T071/T072) → US2 (chat mutate + gate) → US3 (chat reads) → US4 (telemetry sink) →
US5 (teardown). Each adds value without breaking the previous.

---

## Notes

- [P] = different files, no incomplete-task dependency.
- The MCP server **reuses the spec-006 verb layer by project reference** — no verb/dispatch/registry/
  inventory logic is reimplemented (SC-003/SC-009).
- The **reconciler runs only on the `api` node**; it is disabled on the scale-to-zero `mcp` node
  (research §13) — verify in T011/T022.
- The Postgres `pgaadauth` principal creation (T025/T038) is the **single reviewed manual step** (SC-010);
  everything else is OpenTofu-dispatched (Article I).
- Teardown (US5) is an acceptance criterion (Article IV), not optional.
