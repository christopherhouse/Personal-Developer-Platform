# infra/control-plane-host — Azure hosting for the control plane (spec 007)

Stands the spec-006 control plane up as a real, in-Azure service and hosts the `pdp-mcp` chatops server.
A **new, isolated** OpenTofu stack (state key `platform/control-plane-host`) that **consumes** the
existing VNet / private Postgres / private DNS by reference and **never** touches the `prevent_destroy` +
`CanNotDelete` ledger RG — so a dispatched `tofu destroy` here leaves zero residual footprint (FR-016,
SC-010).

## What it provisions (US1/US2/US4)

| Resource | Module / type | SKU / mode |
|---|---|---|
| Resource group `rg-pdp-westus3-controlplane-host` | `azurerm_resource_group` | `pdp-*` tags; **no** `prevent_destroy` (destroyable) |
| ACA managed environment | `Azure/avm-res-app-managedenvironment/azurerm` | workload-profiles, External, VNet-integrated |
| Container apps `ingress` / `api` / `mcp` | `Azure/avm-res-app-containerapp/azurerm` | ingress external / api internal / mcp internal scale-to-zero |
| Container registry | `Azure/avm-res-containerregistry-registry/azurerm` | **Basic** |
| Log Analytics workspace | `Azure/avm-res-operationalinsights-workspace/azurerm` | `PerGB2018`, daily cap |
| Application Insights | `Azure/avm-res-insights-component/azurerm` | workspace-based |
| Key Vault | `Azure/avm-res-keyvault-vault/azurerm` | RBAC; GitHub App key + webhook secret |
| Per-app UAMIs + role assignments | `azurerm_user_assigned_identity`, `azurerm_role_assignment` | see below |

The ACA subnet (`snet-pdp-westus3-aca` `10.0.0.32/27`, delegation `Microsoft.App/environments`) is
**declared by the VNet-owning `infra/control-plane` stack** (T008) and **consumed here** via a data
source — avoiding AVM VNet-module subnet drift (research §10).

## Article V — non-AVM resource justification

`azurerm_user_assigned_identity` and `azurerm_role_assignment` are used directly: **there is no AVM
module for a user-assigned managed identity**, and role assignments are first-class provider resources
(also expressible inline on several AVM modules). All other resources use published AVM modules, pinned
exactly and smoke-validated under OpenTofu 1.11.x (T009).

## Deploy runbook

1. Add the ACA subnet to `infra/control-plane` (T008) and apply that stack (plan-on-PR / apply-on-merge).
2. Seed Key Vault secrets (GitHub App private key + webhook HMAC secret) via the secure pipeline path —
   **never** in tofu vars or state.
3. Apply this stack (plan-on-PR / apply-on-merge); build + push images to ACR over OIDC.
4. **One-time Postgres principal bootstrap** (the single reviewed manual step, SC-010): as the Entra
   admin (owner), run the `pgaadauth_create_principal_with_oid(...)` + `GRANT` commands this stack emits
   as outputs, for `uami-api` and `uami-mcp` (research §6). Idempotent on re-run.

## Teardown (US5)

`controlplane-host-destroy.yml` dispatches a gated `tofu destroy` over this stack (Article VIII typed
confirmation). Then drop the `uami-api` / `uami-mcp` `pgaadauth` principals + revoke grants (the manual
cleanup). The ledger RG is never in scope.

> Spec 007 status: **Phase 1 (Setup)** — stack skeleton (backend / providers / variables / locals /
> data + outputs placeholders). Resources land in Phase 3 (US1) onward.
