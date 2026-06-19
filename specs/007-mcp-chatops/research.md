# Phase 0 Research — MCP Chatops Hosting

All Azure/.NET facts below were verified live via the Microsoft Learn MCP and the MCP C# SDK docs
(context7 / SDK repo) on 2026-06-18, per the project's MCP-first rule. Decisions are recorded as
Decision / Rationale / Alternatives.

---

## §1 — AVM modules for the host stack

**Decision**: Use published AVM (Terraform-flavor) resource modules:
- `Azure/avm-res-app-managedenvironment/azurerm` — Container Apps managed environment (wraps `azapi`;
  supports `vnet_configuration.infrastructure_subnet_id`, internal/external LB, workload profiles,
  `log_analytics_workspace.resource_id`).
- `Azure/avm-res-app-containerapp/azurerm` — each container app (exposes `managed_identities` and
  `registries` with a UAMI `identity` for credential-free pull).
- `Azure/avm-res-containerregistry-registry/azurerm` — ACR (has a `role_assignments` input for AcrPull).
- `Azure/avm-res-operationalinsights-workspace/azurerm` — Log Analytics workspace.
- `Azure/avm-res-insights-component/azurerm` — Application Insights (mandatory `workspace_id` ⇒
  workspace-based).
- `Azure/avm-res-keyvault-vault/azurerm` — Key Vault (RBAC, holds the GitHub App key + webhook secret).

**Rationale**: AVM-first (Article V); all are published, `azurerm` 4.x / `azapi` 2.x compatible, and must
be smoke-validated under pinned OpenTofu 1.11.x before adoption. Pre-1.0 module versions ⇒ pin exactly.

**Alternatives**: Hand-rolled `azurerm_container_app*` etc. — rejected (AVM exists; `azurerm_container_app`
lags the ACA API vs the `azapi`-based AVM module). UAMI/role have **no** AVM module ⇒ use
`azurerm_user_assigned_identity` + `azurerm_role_assignment` directly, justified in the stack README.

---

## §2 — ACA environment type & subnet size

**Decision**: **Workload-profiles** environment (v2) with only the built-in **Consumption** profile.
Carve a **/27** subnet for ACA, delegated to **`Microsoft.App/environments`**. Concrete:
**`10.0.0.32/27`** (`.32`–`.63`) in the existing `vnet-pdp-westus3-controlplane` (`10.0.0.0/24`), which
does not overlap the Postgres `/28` at `.0`–`.15`.

**Rationale**: Workload-profiles is the current recommended environment type and needs only a `/27`
(vs `/23` for legacy Consumption-only). A `/27` supports far more replicas than this 3-app, min-1
workload needs. Aligns on a 32-boundary.

**Alternatives**: Consumption-only (legacy, `/23`) — rejected (wasteful, deprecated path). `/26` for
headroom — acceptable if desired, but `/27` is sufficient and leaves `.64`–`.255` free.

---

## §3 — Private Postgres DNS resolution from ACA

**Decision**: **No new private DNS zone link is needed.** The ACA subnet is in the **same VNet** as the
VNet-injected Postgres, and that VNet is already linked to `privatelink.postgres.database.azure.com` by
the existing `infra/control-plane` stack. Same-VNet ⇒ ACA resolves the Postgres FQDN through the existing
link automatically.

**Rationale**: Azure private-DNS resolution is VNet-scoped; any resource in a linked VNet resolves the
zone. The "add a VNet link" caveat applies only to **other** VNets.

**Alternatives**: A host-stack-owned link — rejected (redundant; would duplicate ownership of a zone the
`dns` RG owns). Watch-out: do **not** set a custom DNS server on the VNet/env without forwarding to
`168.63.129.16`.

---

## §4 — Ingress topology (one public app; clarified 2026-06-18)

**Decision**: The **YARP ingress is the single external-ingress app**. The ACA **environment** is
External (has a public IP, required so GitHub can reach the webhook). YARP routes `/webhooks/github` → the
**internal-ingress** Api and `/mcp` + `/.well-known/oauth-protected-resource` → the **internal-ingress**
MCP server. Api and MCP set `ingress.external = false`. YARP holds no business logic and does **not**
validate the Entra JWT (the MCP server does).

**Rationale**: Per-app ingress visibility is independent in ACA; external + internal coexist (Envoy returns
404 to external hits on internal apps). One public surface = tighter Article IX. Preserves spec-006's
reverse-proxy → internal-handler pattern, now extended to MCP.

**Alternatives**: MCP as its own external app (two public surfaces) — rejected by the owner in favor of
consolidation. APIM — explicit non-goal.

---

## §5 — UAMI → private Postgres (Entra-token auth) in .NET

**Decision**: Each Postgres-touching app (Api, MCP) authenticates with its **user-assigned** identity via
`ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId("<client-id>"))` (NOT bare
`DefaultAzureCredential` in production), token scope
**`https://ossrdbms-aad.database.windows.net/.default`**, supplied to Npgsql via
**`NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider`** (call `credential.GetTokenAsync` per the
provider, ~55-min refresh; set `Username` = the UAMI display name). Reuses the spec-005/006 pluggable
`TokenCredential` seam. **CORRECTION (Phase 1, T003 verification)**: there is **no
`Microsoft.Azure.PostgreSQL.Auth` package on NuGet** (an earlier research draft assumed one) — the Npgsql
periodic-password-provider is the mechanism, needing **no package beyond `Azure.Identity` + the Npgsql
already pulled in by the verb layer**.

**Rationale**: ACA requires the explicit UAMI client id; the periodic provider re-fetches the token per
new pooled connection and refreshes ahead of expiry (UAMI tokens last up to 24h). No password, no
connection-string secret, no extra dependency.

**Alternatives**: System-assigned identity — rejected (owner chose per-app UAMI; SAMI can't be granted
before the app exists, breaking AcrPull-at-create). Manual `GetToken`+set-password — works but the lib is
cleaner and avoids the "username already set" footgun.

---

## §6 — Postgres principal creation for the UAMI (the bootstrap step)

**Decision**: As part of the deploy runbook, the **Entra admin (owner)** runs, once per Postgres-touching
UAMI, on the `postgres` database:
`SELECT * FROM pgaadauth_create_principal_with_oid('<uami-name>', '<uami-object-id>', 'service', false, false);`
then `GRANT` least-privilege on the `ipam` / `registry` schemas. OpenTofu **outputs the exact `psql`
command** (interpolating UAMI name/oid + server FQDN). The Postgres role name = the UAMI display name;
the token matches by **object id**.

**Rationale**: `pgaadauth_*` must run as the Entra admin on a server only reachable in-VNet. A single
reviewed manual step is **explicitly permitted** by FR-016/SC-010, and idempotent on re-run. Matches why
spec-006 deferred T071/T072 (private server) — now resolved for runtime, but the *initial* principal
creation still needs an in-VNet Entra-admin actor.

**Alternatives**: Self-hosted in-VNet GH runner running `null_resource local-exec` — fully automated but
adds a runner dependency beyond spec-7 scope. Transient ACI jump-box — more complexity. A deployment UAMI
set as a second Postgres Entra admin + an ACA bootstrap Job — possible future automation; out of scope now.

---

## §7 — ACR pull with UAMI

**Decision**: Assign **AcrPull** (`azurerm_role_assignment`, scope = ACR id, principal = each app UAMI),
and set the container app `registries = [{ server = "<acr>.azurecr.io", identity = <uami-resource-id> }]`
with the same UAMI in `managed_identities.user_assigned_resource_ids`. ACR **Basic** SKU (no anonymous
pull — exactly the private posture wanted). Ensure ARM-audience tokens are enabled on the registry
(`az acr config authentication-as-arm`; default-on for new registries).

**Rationale**: Credential-free image pull via UAMI is the ACA-native pattern; Basic is the smallest SKU
and inherently private-pull-only.

**Alternatives**: Admin user / registry password — rejected (stored secret). GHCR — rejected at clarify
(would add a non-Azure secret). Standard/Premium SKU — unnecessary cost.

---

## §8 — GitHub App secret delivery (Key Vault)

**Decision**: Store the **pdp-orchestrator GitHub App private key** and the **`workflow_run` webhook HMAC
secret** in a **Key Vault** (AVM, RBAC). The Api and MCP apps reference them as **ACA Key Vault-backed
secrets**, resolved at runtime via the app's UAMI (**Key Vault Secrets User** role) — so no secret value
lands in OpenTofu state or app config. These remain **the only non-Azure secret** (Article IX / charter).

**Rationale**: Key Vault + UAMI keeps the secret out of state/code (cleaner than inline ACA secrets) and
is AVM/Azure-native. The webhook HMAC secret is part of the same GitHub integration.

**Alternatives**: Inline ACA secret — rejected (value persists in tofu state). GitHub Actions secret only
— not reachable by the running ACA apps.

---

## §9 — MCP C# SDK: transport, tools, auth

**Decision**: `ModelContextProtocol.AspNetCore`. Bootstrap:
`AddMcpServer().WithHttpTransport(o => o.Stateless = true).WithTools<…>()`, `app.MapMcp().RequireAuthorization("OwnerOnly")`.
Tools: `[McpServerToolType]` classes, one per verb interface, constructor-injecting `ISpokeVerbs` /
`IFabricVerbs` / `IIpamVerbs` / `IInventoryVerbs` / `IRunVerbs` from DI; `[McpServerTool, Description(...)]`
methods call the verb 1:1; inject `ClaimsPrincipal` for the owner check; `CancellationToken` auto-excluded
from the schema. Auth: `DefaultChallengeScheme = McpAuthenticationDefaults.AuthenticationScheme` +
`AddJwtBearer` (Entra v2.0 authority, explicit `ValidAudience`/`ValidIssuer`, `MapInboundClaims = false`)
+ `.AddMcp(o => o.ResourceMetadata = …)` to publish `/.well-known/oauth-protected-resource`. Owner gate =
`RequireClaim("oid", ownerOid)` policy.

**Rationale**: **Stateless** transport fits ACA scale-to-zero (no session affinity; any replica/cold-start
serves any call) and needs no back-channel. The PRM challenge lets Claude auto-discover the Entra auth
flow. `MapInboundClaims = false` preserves the short `oid` claim.

**Alternatives**: Stateful + SSE (needed only for sampling/elicitation) — rejected (not needed; breaks
scale-to-zero affinity). `WithToolsFromAssembly()` — rejected in favor of explicit `WithTools<T>()` for an
intentional surface. APIM/EasyAuth for auth — rejected (server-handled auth, no APIM).

---

## §10 — Cross-stack subnet ownership

**Decision**: **Declare** the ACA subnet inside the VNet-owning `infra/control-plane` stack (in the AVM
`avm-res-network-virtualnetwork` `subnets` map, or its `//modules/subnet` submodule), **output** its id,
and **consume** it in `infra/control-plane-host` via an `azurerm_subnet` **data source** (by name + VNet),
passed to the ACA env's `infrastructure_subnet_id`.

**Rationale**: The AVM VNet module manages its `subnets` map and does **not** `ignore_changes` — a subnet
created out-of-band would be deleted on the next VNet apply (destructive drift). A data source is looser
than `terraform_remote_state` (no upstream state access) and fails clearly at plan time if renamed.

**Alternatives**: `terraform_remote_state` — rejected (tight coupling, needs state access). `azurerm_subnet`
in the host stack targeting the AVM-managed VNet — rejected (drift).

---

## §11 — Smallest viable SKUs / cost

**Decision**: ACA **Consumption** profile (no Dedicated profile → no management fee; VNet injection, not
private endpoints, so no PE management fee); Log Analytics **PerGB2018** with a **daily cap** (e.g. 1 GB);
**workspace-based** App Insights (inherits LAW pricing); **ACR Basic** (~$5/mo); **Standard** Key Vault.
Always-on (min-1) Api + YARP bill mostly at idle rates, largely within the ACA free grant at this scale.

**Rationale**: Article IX "cheap by default." MCP scale-to-zero costs nothing idle.

**Alternatives**: Dedicated workload profile / Standard ACR / Premium KV — unnecessary cost.

---

## §12 — Plan/confirm surfaced through MCP tools (Article VIII)

**Decision**: Two-tool pattern per mutating op. `Plan<Op>` returns the verb-layer plan **plus a
confirmation token** (signed, single-use, ~5-min TTL, encoding `{operation, targetName}`). `Apply<Op>` /
`Destroy<Op>` requires the token **and** a `target` parameter that must equal the token's target verbatim;
mismatch/expiry ⇒ rejected. The token is issued/validated server-side (not derivable by the model). The
underlying gate is the spec-006 verb-layer two-phase flow — the MCP adapter **surfaces** it, does not
reimplement it. Tool `[Description]`s state the confirmation requirement explicitly and never call a
destroy "safe."

**Rationale**: Stateless transport rules out MCP elicitation (needs a back-channel). The two-tool +
restated-target design makes it **impossible** for a chat turn to destroy without an explicit confirming
call carrying the verbatim target (Article VIII; FR-013, SC-002).

**Alternatives**: MCP elicitation — rejected (requires stateful SSE). A single tool with a `confirm=true`
flag — rejected (a model could set it without human intent; not "unbypassable").

---

## §13 — Decided: in-process verb hosting; the Api is the sole tracking node

**Decision (confirmed 2026-06-18)**: Keep the **in-process** design. The MCP server hosts the verb layer
in-process exactly as the `pdp` CLI does (dispatch half); the **`Pdp.ControlPlane.Api` is the sole,
always-on run-tracking host** — the `workflow_run` webhook sink + the durable inbox/outbox + the lifecycle
saga + the polling reconciler. The Api has **no verb HTTP endpoints**; its HTTP surface exists only to
receive the GitHub webhook (behind YARP at `/webhooks/github`). There is **no MCP→Api HTTP call**; the only
internal HTTP is YARP→Api (webhook) and YARP→MCP (`/mcp`).

**Binding implementation constraints** (for `/speckit-tasks`):
- The MCP server and the Api are **two Wolverine nodes sharing the same Postgres** (`wolverine` schema /
  durable outbox / saga store). This is supported.
- The **scheduled polling reconciler MUST run on the Api node only** — it must be disabled on the MCP node
  to avoid duplicate sweeps. (MCP dispatches + records intent + reads; it does **not** run the reconciler.)
- The MCP node still participates in the durable outbox (so a dispatch enqueued there is durable), but
  scheduled/recurring messages are owned by the Api.

**Rationale**: matches the actually-shipped spec-006 wiring (CLI references `Pdp.ControlPlane.Verbs`
directly; the Api is webhook+reconciler only). A scale-to-zero MCP node cannot reliably catch async
webhooks, so a persistent Api node is required regardless.

**Alternative (rejected by the owner, 2026-06-18)**: build verb HTTP endpoints on the Api and make MCP a
thin HTTP client (single Wolverine node, smaller MCP identity with no Postgres/GitHub access). Rejected to
match the CLI pattern and avoid adding an endpoint surface spec 006 did not build.

### Open items deferred to implementation (not blocking)
- Exact ACA CPU/memory per app (start 0.25 vCPU / 0.5 GiB; tune from telemetry).
