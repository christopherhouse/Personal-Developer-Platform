# Archetype — `container-app-sql`

A containerized application on the **spoke's shared Azure Container Apps environment** plus an
**Azure SQL serverless (auto-pause) database** — private by default, credential-free end to end,
one isolated OpenTofu state per workload (`workloads/<sub-id>/<spoke-name>/<workload-name>`).
Deployed only through the platform's workload verbs (`pdp workload deploy` / chat) at a **pinned
git tag** (`archetype/container-app-sql/v<semver>`); never applied by hand (Articles I/II).

## What it creates (target subscription)

| Resource | How | Notes |
|---|---|---|
| RG `rg-pdp-<region>-workload-<name>` | `azurerm_resource_group` | The destroy unit; full `pdp-*` tag set incl. `pdp-workload` + `pdp-env` (FR-013) |
| UAMI `uami-pdp-<region>-wl-<name>` | `azurerm_user_assigned_identity` | App identity + (conditional) ACR pull + **SQL Entra admin** |
| Container app `ca-pdp-<region>-<name>` | AVM `avm-res-app-containerapp` **0.9.0** | On the **spoke's shared env** (cross-RG by ID); `ingress.external = parameters.publicEndpoint` (default **false**); scale-to-zero consumption |
| SQL server `sql-pdp-<region>-<name>` | AVM `avm-res-sql-server` **0.2.1** | Entra-**only** auth, public access **disabled**, PE into the spoke `workload` subnet + `privatelink.database.windows.net` zone group |
| SQL db `sqldb-<name>` | (same module, `databases`) | `GP_S_Gen5_1` serverless, `min_capacity 0.5`, auto-pause per `sqlAutoPauseDelayMinutes`, `max_size_gb` per `sqlMaxSizeGb` |

**Consumes by reference, creates no network fabric (FR-012)**: the spoke remote state
(`spoke_aca_environment_id`, `spoke_subnets["workload"]`), the shared Log Analytics workspace,
the shared SQL privatelink zone, and (conditionally) the platform ACR.

## Parameters

Validated against the version's JSON schema in `archetypes/catalog.json` **before** any dispatch
(FR-003). Defaults live in the module's `parameters` object variable — the control plane never
injects them.

| Parameter | Type / default | Meaning |
|---|---|---|
| `containerImage` | string, **required** | Any resolvable public image ref, or a platform-ACR ref (`crpdp<region>controlplane.azurecr.io/...`) which gets automatic managed-identity pull — credentials are never parameters |
| `publicEndpoint` | bool, `false` | Explicit opt-in for an externally reachable endpoint (visible in the plan) |
| `targetPort` | int, `8080` | Container port ingress targets |
| `cpu` / `memory` | `0.25` / `"0.5Gi"` | Smallest viable by default (Article IX) |
| `sqlAutoPauseDelayMinutes` | int, `60` | Serverless auto-pause delay (compute → $0 paused) |
| `sqlMaxSizeGb` | int, `2` | Database max size |

## Identity & connectivity (R6 — recorded decisions)

- **UAMI as SQL Entra administrator**: the workload's single UAMI is both the app identity and the
  server's Entra admin with `azuread_authentication_only = true`. This sidesteps the
  `CREATE USER ... FROM EXTERNAL PROVIDER` bootstrap (which needs a T-SQL session no pipeline has)
  at the cost of the app holding admin on **its own** server — acceptable for a single-owner,
  one-app-per-server archetype v1; revisit if multi-app-per-server ever matters.
- The app connects with `Authentication=Active Directory Managed Identity` +
  `User Id=<uami client id>` (surfaced to the container as `ConnectionStrings__Sql`) — **zero
  secrets** anywhere, in state or otherwise.
- SQL resolves through the spoke-linked `privatelink.database.windows.net` zone to the private
  endpoint; `public_network_access_enabled = false` means no public path exists at all.

## Article V — raw `azurerm` resources (justification)

- `azurerm_user_assigned_identity` — no AVM composition exists for a single UAMI (same
  justification as `infra/control-plane-host`).
- `azurerm_role_assignment` (conditional AcrPull) — role assignments are first-class provider
  resources; no AVM module.
- `azurerm_resource_group` — no AVM composition for a single RG.

## Article V — AVM smoke validation

| Module | Version (pinned exact) | Result |
|---|---|---|
| `Azure/avm-res-app-containerapp/azurerm` | `0.9.0` | ✅ proven under OpenTofu 1.11 by `infra/control-plane-host` (spec 007) |
| `Azure/avm-res-sql-server/azurerm` | `0.2.1` | ✅ live smoke 2026-07-02 (T027 / quickstart Scenario 2): owner-run one-off `init/plan/apply/destroy` of a minimal serverless config under OpenTofu **1.11.6**, local state, throwaway RG `rg-pdp-westus3-smoke-sql` in the platform sub. Clean apply AND destroy: `GP_S_Gen5_1` + `min_capacity 0.5` + `auto_pause_delay_in_minutes 60` accepted; Entra-**only** admin with no `administrator_login` provisions cleanly; `public_network_access_enabled = false` applies without a PE. Gate for T034 **open**. |

(The spoke's shared ACA environment uses `avm-res-app-managedenvironment` 0.4.0 — pinned and
justified in `infra/spoke`; 0.5.0 requires Terraform ~>1.12, incompatible with OpenTofu 1.11.x.)

## Releasing a version

Per `archetypes/README.md`: edit `archetypes/catalog.json` (append-only, content-immutable) **and**
create the tag `archetype/container-app-sql/v<semver>` in the same PR/release step. Deployed
workloads keep their stamped tag forever (FR-005); destroy checks out the stamped tag so teardown
semantics match what was applied (R7).
