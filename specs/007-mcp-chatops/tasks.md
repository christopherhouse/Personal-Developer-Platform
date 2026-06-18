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

- [ ] T008 Edit `infra/control-plane/main.tf`: add subnet `snet-pdp-westus3-aca` `10.0.0.32/27` (delegation `Microsoft.App/environments`) to the AVM VNet module's `subnets` map; add its id to `infra/control-plane/outputs.tf` (research §10 — avoids AVM VNet-module subnet drift)
- [ ] T009 Smoke-validate + pin exact versions of the AVM modules under OpenTofu 1.11.x (Article V): `avm-res-app-managedenvironment`, `avm-res-app-containerapp`, `avm-res-containerregistry-registry`, `avm-res-operationalinsights-workspace`, `avm-res-insights-component`, `avm-res-keyvault-vault`; record results + the two `azurerm` UAMI/role justifications in `infra/control-plane-host/README.md`
- [ ] T010 Implement `infra/control-plane-host/data.tf`: data sources for the ACA subnet (by name + VNet), the Postgres server/FQDN, and the platform-dns RG; `tofu validate` passes
- [ ] T011 Implement `src/Pdp.Mcp/Program.cs` base bootstrap: `AddMcpServer().WithHttpTransport(o => o.Stateless = true)`; `AddControlPlaneVerbs(...)` (DI the spec-006 verb layer in-process) with `ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(...))`; `UseAzureMonitor()` reading `APPLICATIONINSIGHTS_CONNECTION_STRING` (graceful if empty); **disable Wolverine scheduled/reconciler messages on this node** (research §13 — the Api owns the reconciler)
- [ ] T012 Implement Postgres Entra-token auth wiring in `src/Pdp.Mcp/` (and shared seam): Npgsql `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider` calling `Azure.Identity` `GetTokenAsync` (scope `https://ossrdbms-aad.database.windows.net/.default`, ~55-min refresh; Username = UAMI display name) — **no** `Microsoft.Azure.PostgreSQL.Auth` package (does not exist, research §5)
- [ ] T013 Implement `src/Pdp.Mcp/Auth/OwnerAuthorization.cs`: JWT bearer (Entra v2.0 authority, explicit `ValidAudience`/`ValidIssuer`/`ValidateLifetime`, `MapInboundClaims = false`) + `.AddMcp(o => o.ResourceMetadata = ...)` PRM + `"OwnerOnly"` policy `RequireClaim("oid", ownerOid)`; `app.MapMcp().RequireAuthorization("OwnerOnly")` (contracts/identity-and-auth.md)

**Checkpoint**: the MCP host starts with auth wired (no tools yet); the host stack validates against the live VNet/Postgres data sources.

---

## Phase 3: User Story 1 — Control plane runs in-Azure, in-VNet, reaching the private ledger (Priority: P1) 🎯 MVP

**Goal**: host `Pdp.ControlPlane.Api` + `Pdp.ControlPlane.Ingress` on ACA, VNet-integrated, reaching the
private Postgres via per-app UAMI Entra-token auth — unblocking spec-006 T071/T072.

**Independent Test**: the hosted `api` authenticates to the private Postgres with `uami-api` (Entra token,
no password) and reads the ledger/registry; spec-006 T071/T072 run live against the hosted control plane.

- [ ] T014 [US1] Implement RG + tags + per-app UAMIs (`uami-pdp-westus3-{ingress,api,mcp}`) via `azurerm_user_assigned_identity` in `infra/control-plane-host/main.tf` (RG carries the `pdp-*` tag schema; **no `prevent_destroy`**)
- [ ] T015 [P] [US1] Implement ACR (Basic SKU) via `avm-res-containerregistry-registry` + `AcrPull` role assignments to all three UAMIs in `infra/control-plane-host/main.tf` (research §7)
- [ ] T016 [P] [US1] Implement Key Vault via `avm-res-keyvault-vault` (RBAC) holding `github-app-private-key` + `github-webhook-secret`; grant `Key Vault Secrets User` to `uami-api` (+ `uami-mcp`); seed secret values via the secure pipeline path, never tofu vars/state (research §8)
- [ ] T017 [P] [US1] Implement Log Analytics workspace via `avm-res-operationalinsights-workspace` (`PerGB2018`, daily cap ~1 GB) in `infra/control-plane-host/main.tf` (required by the ACA env)
- [ ] T018 [US1] Implement the ACA managed environment via `avm-res-app-managedenvironment` (workload-profiles, External, `infrastructure_subnet_id` = ACA subnet data source, `log_analytics_workspace.resource_id` = T017) in `infra/control-plane-host/main.tf`
- [ ] T019 [US1] Implement `azurerm_role_assignment` granting `uami-api` subscription `Reader` (Azure Resource Graph reconcile reads) in `infra/control-plane-host/main.tf`
- [ ] T020 [US1] Implement the `api` container app via `avm-res-app-containerapp` (**internal** ingress, min replicas **1**, `uami-api`, ACR image via UAMI, KV-backed secrets for GitHub App key + webhook secret, Postgres FQDN config) in `infra/control-plane-host/main.tf`
- [ ] T021 [US1] Implement the `ingress` (YARP) container app via `avm-res-app-containerapp` (**external** ingress, min replicas **1**, `uami-ingress`, ACR image) and edit `src/Pdp.ControlPlane.Ingress/appsettings.json` to route `POST /webhooks/github` → internal `api`
- [ ] T022 [US1] Add `env_id`/Postgres-token config + verify the hosted `Api` reconciler/webhook handler run (always-on) against the live ledger; wire `ManagedIdentityCredential` client id for `uami-api`
- [ ] T023 [US1] Implement `outputs.tf` (ACR login server, app FQDNs, UAMI names/object-ids/client-ids) + emit the **one-time `pgaadauth_create_principal_with_oid` psql command** for `uami-api` as a tofu output (research §6)
- [ ] T024 [US1] Author `.github/workflows/controlplane-host-images.yml`: build + push `api`/`ingress` (and `mcp`) images to ACR over **OIDC** (no registry secret)
- [ ] T025 [US1] Run the one-time bootstrap for `uami-api`: `pgaadauth_create_principal_with_oid(...)` + least-privilege `GRANT` on `ipam`+`registry` (owner/Entra-admin; documented in `infra/control-plane-host/README.md`)
- [ ] T026 [US1] Deploy via the CI rails (plan-on-PR / apply-on-merge for `infra/control-plane`subnet then `infra/control-plane-host`) and verify the hosted `api` reaches the private ledger (Entra token, no password) — quickstart Scenarios 0–1
- [ ] T027 [US1] Run spec-006 quickstart **T071/T072** against the hosted control plane (live spoke vend/destroy on `westus3`, tracked to terminal) — quickstart Scenario 7

**Checkpoint**: the control plane runs in-Azure, reaches the private ledger via UAMI, and the spec-006 live acceptance is unblocked and passing.

---

## Phase 4: User Story 2 — Vend then destroy a spoke through chat, with the plan/confirm gate (Priority: P1)

**Goal**: the `pdp-mcp` server exposes vend/destroy verbs as Entra-gated MCP tools with the Article VIII
plan→confirm gate; deploy the `mcp` container app behind YARP.

**Independent Test**: from an Entra-authenticated MCP client, vend a spoke (plan surfaced before apply)
then destroy it (refused without token + verbatim target restatement) — all through chat.

### Tests for User Story 2 ⚠️ (write first; ensure they fail before implementation)

- [ ] T028 [P] [US2] Auth tests in `tests/Pdp.Mcp.Tests/` (`WebApplicationFactory`): unauthenticated `/mcp` → 401 + `WWW-Authenticate` PRM challenge; owner `oid` → 200; **non-owner `oid` → rejected** (SC-006)
- [ ] T029 [P] [US2] Confirmation-token tests in `tests/Pdp.Mcp.Tests/`: plan→confirm flow, **verbatim target restatement required**, single-use + expiry, operation-mismatch rejected (Article VIII, SC-002)
- [ ] T030 [P] [US2] Tool→verb adapter tests in `tests/Pdp.Mcp.Tests/` (NSubstitute `ISpokeVerbs`/`IFabricVerbs`): each tool calls its verb 1:1, no logic, structured result passthrough (SC-003)

### Implementation for User Story 2

- [ ] T031 [P] [US2] Implement `src/Pdp.Mcp/Confirm/IConfirmationTokens.cs` + `ConfirmationTokenService.cs`: signed/opaque, single-use, ~5-min TTL, binds `{operation, targetName}` (data-model §5)
- [ ] T032 [US2] Implement `src/Pdp.Mcp/Tools/SpokeTools.cs` ([McpServerToolType], ctor-inject `ISpokeVerbs` + `IConfirmationTokens`): `PlanSpokeVend`/`ApplySpokeVend`, `PlanSpokeDestroy`/`DestroySpoke` (token + verbatim name); `EnsureOwner(ClaimsPrincipal)` (contracts/mcp-tool-surface.md)
- [ ] T033 [US2] Implement `src/Pdp.Mcp/Tools/FabricTools.cs` (ctor-inject `IFabricVerbs`): `PlanFabricCreate`/`ApplyFabricCreate`, `PlanFabricDestroy`/`DestroyFabric` (token + verbatim name)
- [ ] T034 [US2] Register the tool classes in `src/Pdp.Mcp/Program.cs` (`.WithTools<SpokeTools>().WithTools<FabricTools>()`); confirm T028–T030 pass
- [ ] T035 [P] [US2] Implement the `mcp` container app via `avm-res-app-containerapp` (**internal** ingress, **min replicas 0** scale-to-zero, `uami-mcp`, ACR image, KV-backed GitHub App key, AzureAd + OwnerOid + App Insights config) in `infra/control-plane-host/main.tf`
- [ ] T036 [US2] Implement `azurerm_role_assignment` granting `uami-mcp` subscription `Reader`; add `uami-mcp` `pgaadauth` bootstrap to the tofu output + `README.md` (research §6)
- [ ] T037 [US2] Edit `src/Pdp.ControlPlane.Ingress/appsettings.json`: add YARP routes `/mcp` and `/.well-known/oauth-protected-resource` → internal `mcp` (streamable-HTTP passthrough; forward `Authorization` header; no business logic) (contracts/hosting-topology.md)
- [ ] T038 [US2] Run the one-time bootstrap for `uami-mcp` (`pgaadauth_create_principal_with_oid` + `GRANT` on `ipam`+`registry`)
- [ ] T039 [US2] Deploy (images + `tofu apply`) and verify the MCP auth gate live — quickstart Scenario 2
- [ ] T040 [US2] Live: vend a spoke through an MCP client with the plan surfaced before apply, tracked to terminal — quickstart Scenario 3
- [ ] T041 [US2] Live: destroy the spoke — refused without token + verbatim name, then succeeds (allocation released, env `destroyed`) — quickstart Scenario 4

**Checkpoint**: the owner can vend and destroy a spoke end-to-end through chat with the unbypassable Article VIII gate.

---

## Phase 5: User Story 3 — Conversational reads: inventory, IPAM, intent/run history (Priority: P2)

**Goal**: expose the read verbs as MCP tools — "what's deployed?" (ARG), IPAM query, intent/run history.

**Independent Test**: from the MCP client, ask the inventory / IPAM / history questions; answers are
correctly sourced (deployed ⇒ ARG; intent/history ⇒ registry) with no duplicated query logic.

### Tests for User Story 3 ⚠️

- [ ] T042 [P] [US3] Read-tool tests in `tests/Pdp.Mcp.Tests/` (NSubstitute `IIpamVerbs`/`IInventoryVerbs`/`IRunVerbs`): each tool calls its verb 1:1; division-of-truth assertion (inventory ⇒ ARG vs registry reads) (SC-003/SC-004)

### Implementation for User Story 3

- [ ] T043 [P] [US3] Implement `src/Pdp.Mcp/Tools/IpamTools.cs` (ctor-inject `IIpamVerbs`): `QueryIpam`
- [ ] T044 [P] [US3] Implement `src/Pdp.Mcp/Tools/InventoryTools.cs` (ctor-inject `IInventoryVerbs`): `WhatsDeployed`/`ListEnvironments` (ARG)
- [ ] T045 [P] [US3] Implement `src/Pdp.Mcp/Tools/RunTools.cs` (ctor-inject `IRunVerbs`): `ShowEnvironment`/`RunHistory`/`RunStatus`
- [ ] T046 [US3] Register the read-tool classes in `src/Pdp.Mcp/Program.cs` (`.WithTools<IpamTools>().WithTools<InventoryTools>().WithTools<RunTools>()`); confirm T042 passes
- [ ] T047 [US3] Live: ask "what's deployed?" / IPAM / "what did I ask, what happened?" and verify correct sourcing — quickstart Scenario 5

**Checkpoint**: conversational reads work and preserve division of truth.

---

## Phase 6: User Story 4 — env_id-correlated telemetry in Application Insights (Priority: P2)

**Goal**: provision the App Insights sink and wire the apps to export the telemetry they already emit.

**Independent Test**: trigger a verb/run, then trace it end-to-end from a single `env_id` in App Insights.

- [ ] T048 [US4] Implement Application Insights via `avm-res-insights-component` (workspace-based; `workspace_id` = the US1 Log Analytics workspace) in `infra/control-plane-host/main.tf`
- [ ] T049 [US4] Set `APPLICATIONINSIGHTS_CONNECTION_STRING` on the `api` and `mcp` container apps (from the App Insights resource output) so `UseAzureMonitor()` exports there
- [ ] T050 [US4] Deploy and verify `env_id`-correlated logs + traces land in App Insights (verb → dispatch → run-state transitions → terminal) — quickstart Scenario 6
- [ ] T051 [US4] Confirm telemetry-export failure is non-fatal (app operates if App Insights is unreachable) — spec edge case

**Checkpoint**: a run is traceable end-to-end from one `env_id` in Application Insights.

---

## Phase 7: User Story 5 — The whole spec-7 footprint tears down cleanly (Priority: P3)

**Goal**: a dispatched `tofu destroy` over the host stack leaves zero residual footprint; the protected
ledger RG is untouched.

**Independent Test**: dispatch the destroy, run the manual principal cleanup, verify via Resource Graph
that nothing spec-7 remains and the ledger RG is intact.

- [ ] T052 [US5] Author `.github/workflows/controlplane-host-destroy.yml`: manual, **gated** `tofu destroy` over `infra/control-plane-host` (typed confirmation matching the stack name; Article VIII)
- [ ] T053 [US5] Document the manual teardown cleanup in `infra/control-plane-host/README.md`: drop the `uami-api`/`uami-mcp` `pgaadauth` principals + revoke grants (the single reviewed manual step, SC-010)
- [ ] T054 [US5] Verify (live) the dispatched destroy removes all spec-7 resources (ACA env+apps, 3 UAMIs, ACR, Log Analytics, App Insights, Key Vault) — quickstart Scenario 8
- [ ] T055 [US5] Verify via Resource Graph that the `prevent_destroy`+`CanNotDelete` ledger RG is **untouched** and no dangling role grants / orphaned Entra registrations remain (SC-010)

**Checkpoint**: the spec-7 footprint is fully destroyable; the ledger is protected.

---

## Phase 8: Polish & Cross-Cutting Concerns

**Purpose**: docs, pins, and final validation across stories.

- [ ] T056 [P] Record the new pins (`ModelContextProtocol.AspNetCore` 0.9.0-preview.2, `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.9) and the ACA/ACR/KV/App-Insights SKUs in `docs/tech-stack.md` (Postgres auth = Npgsql provider, no package)
- [ ] T057 [P] Finalize `infra/control-plane-host/README.md`: AVM module list + versions, the two `azurerm` UAMI/role justifications (Article V), and the deploy runbook (KV seeding + principal bootstrap)
- [ ] T058 Run `tofu fmt` + `tofu validate` on `infra/control-plane-host` and `infra/control-plane`; `dotnet format` + `dotnet build` + `dotnet test` green
- [ ] T059 Execute the full `quickstart.md` (Scenarios 0–8) end-to-end against `westus3`
- [ ] T060 Update `docs/spec-backlog.md` Status: mark spec 7 progress and note spec-006 T071/T072 unblocked
- [ ] T061 [P] Verify **no inline secret** lands in the rendered tofu plan/state or container-app config for `infra/control-plane-host` (GitHub App key + webhook secret resolve only via Key Vault reference + UAMI; no registry password) — assert against the plan output and `docs`/runbook (SC-007)

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
