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

## AVM module pins (T009, Article V)

Published AVM (Terraform-flavor) resource modules, pinned **exactly** — all are pre-1.0, so a minor bump
can change inputs/behavior (research §1). Versions are the latest published as of 2026-06-18:

| Module | Pinned version | Used by |
|---|---|---|
| `Azure/avm-res-app-managedenvironment/azurerm` | `0.4.0` | ACA managed environment (T018) |
| `Azure/avm-res-app-containerapp/azurerm` | `0.9.0` | ingress / api / mcp container apps (T020/T021/T035) |
| `Azure/avm-res-containerregistry-registry/azurerm` | `0.5.1` | ACR Basic + AcrPull (T015) |
| `Azure/avm-res-operationalinsights-workspace/azurerm` | `0.5.1` | Log Analytics workspace (T017) |
| `Azure/avm-res-insights-component/azurerm` | `0.4.0` | Application Insights, workspace-based (T048) |
| `Azure/avm-res-keyvault-vault/azurerm` | `0.10.2` | Key Vault, RBAC (T016) |

**Smoke validation (Article V)**: each module block is added with its exact pin in US1/US2/US4
(T015–T021, T035, T048) and smoke-validated under the pinned OpenTofu 1.11.x via `tofu init` +
`tofu validate` on this stack at that point — the same inline-smoke-finding discipline the
`infra/control-plane` stack used for its Postgres/VNet AVM modules (e.g. the firewall-rules override
recorded there). Any input/default surprise found at smoke time is recorded against the offending module
block here. Provider pins (`azurerm ~> 4.77`, `azapi ~> 2.7`) are in `versions.tf`.

**Smoke findings (T015–T021, US1):**

- **`avm-res-app-managedenvironment` pinned to `0.4.0`, NOT `0.5.0`.** v0.5.0's `managed_certificates`
  and `storages` submodules declare `required_version = "~> 1.12"`, which fails `tofu init` under our
  constitution-pinned OpenTofu 1.11.x. v0.4.0 allows `>= 1.10, < 2.0` across root + all submodules. Its
  input surface differs: a **flat `infrastructure_subnet_id`** (no `vnet_configuration` object, and no
  `internal` flag — with a subnet the environment defaults to the EXTERNAL public LB, which is exactly the
  posture we want so GitHub can reach the webhook); **`workload_profile`** (singular `set`) instead of
  `workload_profiles`; **`zone_redundancy_enabled`** instead of `zone_redundant`. Output `resource_id` is
  unchanged. This is the documented bump; revisit when a ≥0.5.x line restores OpenTofu 1.11 support.
- **`avm-res-app-containerapp` `0.9.0`** is 1.11-compatible (`required_version = "~> 1.11"`, no submodules).
  Note: its app-URL output is **`fqdn_url`** (a full `https://…` URL), not a bare `fqdn`; `secrets` is a
  `map(object)`; credential-free pull uses `registries[].identity` (a UAMI resource id).
- **Key Vault secret values are NOT declared in this stack.** The `avm-res-keyvault-vault` module writes
  any `secrets_value` into tofu state; to satisfy SC-007 (no secret in state) we provision the vault + RBAC
  only and seed the two secret values out-of-band (runbook step 2). The apps reference them by constructed
  versionless KV URI (`<vault-uri>secrets/<name>`), which ACA resolves at runtime via the app UAMI.

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
