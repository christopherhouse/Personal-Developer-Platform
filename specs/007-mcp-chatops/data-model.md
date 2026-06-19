# Phase 1 Data Model — MCP Chatops Hosting

**No new database entities.** This spec adds Azure infrastructure and an MCP adapter; it reuses the
spec-006 Postgres schemas (`ipam`, `registry`, `wolverine`) **unchanged**. The "model" here is therefore
(a) the Azure **resource & identity** model the `infra/control-plane-host` stack creates, (b) the **auth
principal** model, and (c) the **MCP tool ⇄ verb** mapping. See `contracts/` for the binding shapes.

---

## 1. Azure resources (new stack `infra/control-plane-host`)

| Resource | Module / type | Key config | Lifecycle |
|---|---|---|---|
| Resource group `rg-pdp-westus3-controlplane-host` | `azurerm_resource_group` | `pdp-*` tag schema; `pdp-managed=true` | **Destroyable** (no `prevent_destroy`) |
| ACA managed environment | `avm-res-app-managedenvironment` | workload-profiles; `infrastructure_subnet_id` = ACA subnet (data); External (public IP); `log_analytics_workspace.resource_id` | Destroyable |
| Container app `ca-pdp-westus3-ingress` | `avm-res-app-containerapp` | external ingress; min replicas **1**; YARP image; UAMI `uami-ingress` | Destroyable |
| Container app `ca-pdp-westus3-api` | `avm-res-app-containerapp` | **internal** ingress; min replicas **1**; Api image; UAMI `uami-api`; KV-backed secrets (GitHub App key, webhook secret); App Insights conn string | Destroyable |
| Container app `ca-pdp-westus3-mcp` | `avm-res-app-containerapp` | **internal** ingress; **scale-to-zero** (min 0); MCP image; UAMI `uami-mcp`; KV-backed secret (GitHub App key); App Insights conn string; AzureAd + OwnerOid config | Destroyable |
| Container registry `crpdpwestus3…` | `avm-res-containerregistry-registry` | **Basic** SKU; AcrPull role to the 3 UAMIs | Destroyable |
| Log Analytics workspace | `avm-res-operationalinsights-workspace` | `PerGB2018`; daily cap (~1 GB) | Destroyable |
| Application Insights | `avm-res-insights-component` | **workspace-based** (`workspace_id`) | Destroyable |
| Key Vault `kv-pdp-westus3-cph` | `avm-res-keyvault-vault` | RBAC; holds `github-app-private-key`, `github-webhook-secret`; Key Vault Secrets User → `uami-api`,`uami-mcp` | Destroyable |
| `uami-pdp-westus3-{ingress,api,mcp}` | `azurerm_user_assigned_identity` | one per app | Destroyable |
| Role assignments | `azurerm_role_assignment` | see §2 | Destroyable |

**Edit to existing `infra/control-plane`** (VNet-owning stack): add subnet `snet-pdp-westus3-aca`
`10.0.0.32/27`, delegation `Microsoft.App/environments`, to the AVM VNet module's `subnets` map; output its
id. This is the only change to the protected stack and creates no resource in the protected RG beyond a
subnet inside the existing VNet.

**Consumed by reference (data sources, not created)**: the ACA subnet id, the Postgres server FQDN, the
`privatelink.postgres.database.azure.com` zone's existing VNet link (no new link), the platform-dns RG.

---

## 2. Identity & RBAC principal model (per-app, least privilege)

| Identity | Postgres principal | Azure RBAC | Key Vault | Purpose |
|---|---|---|---|---|
| `uami-api` | **Yes** — `pgaadauth` principal, `GRANT` on `ipam`+`registry` | **AcrPull** (ACR), **Reader** (subscription, for ARG reconcile reads) | **Secrets User** (GitHub App key + webhook secret) | Webhook handling + polling reconcile + saga; needs ledger/registry + GitHub poll + ARG |
| `uami-mcp` | **Yes** — `pgaadauth` principal, `GRANT` on `ipam`+`registry` | **AcrPull** (ACR), **Reader** (subscription, for inventory verbs) | **Secrets User** (GitHub App key) | Hosts the verb layer in-process (like the CLI): validate→allocate→record→dispatch + reads |
| `uami-ingress` | **No** | **AcrPull** (ACR) only | **No** | YARP reverse proxy — forwards only; holds no data-plane credential |

Notes:
- Both `uami-api` and `uami-mcp` are Postgres principals because **both host the verb layer in-process**
  (confirmed against spec-006: `Pdp.Cli` references `Pdp.ControlPlane.Verbs` directly; there are no Api
  verb HTTP endpoints). The MCP server is "exactly as the CLI is."
- "Reader at subscription" satisfies Azure Resource Graph; a custom Resource-Graph-Reader role is a tighter
  optional alternative.
- The **owner** remains the Postgres Entra admin and runs the one-time `pgaadauth_create_principal_with_oid`
  for `uami-api` and `uami-mcp` (research §6).

---

## 3. Auth principals (MCP OAuth 2.1 protected resource)

| Principal | Represents | Validation |
|---|---|---|
| MCP app registration (`api://pdp-mcp` / client id) | the protected resource (audience) | `ValidAudience` on the JWT |
| Entra tenant v2.0 issuer | the authorization server | `ValidIssuer = https://login.microsoftonline.com/{tenant}/v2.0` |
| **Owner** (`oid` GUID) | the single allowed caller | `RequireClaim("oid", ownerOid)` policy on `MapMcp()` |

`MapInboundClaims = false` preserves the short `oid` claim. The PRM document at
`/.well-known/oauth-protected-resource` advertises the issuer + `mcp:tools` scope so the client can
discover the flow from the `WWW-Authenticate` challenge.

---

## 4. MCP tool ⇄ verb mapping (1:1; no new capability)

Each tool is a thin call into an existing spec-006 verb interface. Read tools execute and return; mutating
ops are split into a **Plan** tool and an **Apply/Destroy** tool (Article VIII, §12 / contracts).

| MCP tool | Verb (spec 006) | Kind |
|---|---|---|
| `PlanSpokeVend` / `ApplySpokeVend` | `ISpokeVerbs.Plan/Apply Create` | mutate (plan→confirm) |
| `PlanSpokeDestroy` / `DestroySpoke` | `ISpokeVerbs.Plan/Apply Destroy` | **destroy** (token + restate target) |
| `PlanFabricCreate` / `ApplyFabricCreate` | `IFabricVerbs.Plan/Apply Create` | mutate (plan→confirm) |
| `PlanFabricDestroy` / `DestroyFabric` | `IFabricVerbs.Plan/Apply Destroy` | **destroy** (token + restate target) |
| `QueryIpam` | `IIpamVerbs.Query` | read |
| `WhatsDeployed` / `ListEnvironments` | `IInventoryVerbs.*` (ARG) | read (division of truth) |
| `ShowEnvironment` / `RunHistory` / `RunStatus` | `IRunVerbs.* / registry reads` | read (intent/audit) |

Tool descriptions state the confirmation requirement; identity is read only from the validated JWT
(`ClaimsPrincipal`), never from tool arguments.

---

## 5. Confirmation token (in-memory, MCP host)

| Field | Type | Notes |
|---|---|---|
| `operation` | enum | e.g. `SpokeDestroy`, `FabricCreate` — bound at issue |
| `targetName` | string | the env/spoke/fabric name the plan was generated for |
| `expiresAt` | timestamp | ~5 min TTL |
| (token value) | signed string / opaque id | single-use; removed on first validation |

`Apply/Destroy` rejects (`McpException`) if the token is missing, expired, already used, the operation
mismatches, or the supplied `target` ≠ `targetName`. This realizes "a chat request can never destroy
without an explicit, target-restating confirmation" (FR-013).
