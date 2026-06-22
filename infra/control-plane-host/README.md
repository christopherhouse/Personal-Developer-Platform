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

**Smoke finding (T048, US4):**

- **`avm-res-insights-component` `0.4.0`** validates cleanly under OpenTofu 1.11.6 (no submodule version
  conflicts). Required inputs: `name`, `location`, `resource_group_name`, `workspace_id` (= the T017 Log
  Analytics `resource_id`, making it workspace-based); `application_type` defaults to `"web"`. Its
  `connection_string` output is **sensitive** — it is wired directly onto the `api`/`mcp` apps as the
  `APPLICATIONINSIGHTS_CONNECTION_STRING` env var (a plain env: an Azure-generated ingestion credential,
  not one of the SC-007 non-Azure secrets) and surfaced as a `sensitive` stack output.

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
4. **One-time schema migration** — apply the `ipam` + `registry` EF schemas to the `pdp` database
   (created by the `infra/control-plane` Tofu `databases` block) so the principal grants have tables to
   target. Like the bootstrap, this runs in-VNet as the Entra **admin** via a transient ACA Job:

   ```powershell
   az login                              # as the Postgres Entra ADMIN
   cd infra/control-plane-host
   ./scripts/run-migrations.ps1          # applies scripts/migrations/{ipam,registry}.sql, idempotent
   ```

   The SQL is `dotnet ef migrations script --idempotent` output committed under `scripts/migrations/`;
   regenerate it whenever the EF migrations change. Tables are created **owned by the admin**; the apps
   get DML via the principal grants (next step). The `wolverine` message-store schema is **not** migrated
   here — the always-on `api` owns it and Wolverine auto-builds its tables on startup (the bootstrap
   creates the schema `AUTHORIZATION uami-api`).
5. **One-time Postgres principal bootstrap** (the single reviewed manual step, SC-010): registers
   `uami-api` / `uami-mcp` as Postgres Entra principals + grants them on `ipam`/`registry` (and, for
   `uami-mcp`, role membership in `uami-api` for the api-owned `wolverine` store), so their token logins
   succeed (research §6). **Run AFTER step 4** (grants target the migrated tables). `pgaadauth_*` must run
   as the Entra **admin** against the private ledger — which is VNet-only. Two ways to do it:

   - **Recommended — the transient-Job helper** (run from your laptop/Cloud Shell; the psql runs in-VNet
     inside a short-lived ACA Job, your oss-rdbms token rides in as a Job secret, the Job self-deletes).
     Two equivalent ports — use the one native to your shell:

     ```powershell
     # Windows / PowerShell 7+:
     az login                              # as the Postgres Entra ADMIN
     cd infra/control-plane-host
     ./scripts/bootstrap-postgres-principals.ps1       # both api + mcp (idempotent)
     ```

     ```bash
     # Linux / macOS / Cloud Shell / Git Bash:
     az login
     cd infra/control-plane-host
     ./scripts/bootstrap-postgres-principals.sh        # both api + mcp (idempotent)
     ```

     Both read the `bootstrap_context` output for the RG / ACA env / ledger FQDN, print the exact SQL for
     review, run it in-VNet, and verify the execution Succeeded. Override the psql image with the
     `-PgBootImage` param (PS) / `PG_BOOT_IMAGE` env (bash) if Docker Hub pulls are blocked.

   - **Manual fallback** — from any in-VNet shell as the Entra admin, paste the SQL the stack emits:
     `tofu output -raw pgaadauth_bootstrap_uami_api` (and `…_mcp`), connecting passwordless with your
     oss-rdbms token (`PGPASSWORD=$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)`).

   Both paths are idempotent on re-run.

## Teardown (US5)

This footprint is **destroyable by design** (Article IV, FR-016): the RG carries no `prevent_destroy` /
management lock, and the stack consumes the VNet / private Postgres / private DNS by reference — so a
dispatched destroy removes only the host resources and never touches the `prevent_destroy` +
`CanNotDelete` ledger RG.

1. **Dispatch the gated destroy** — run `controlplane-host-destroy.yml` (Article VIII typed confirmation:
   type `control-plane-host`). It runs `tofu plan -destroy` (shown for the record) then
   `tofu destroy -auto-approve` over `infra/control-plane-host`, tearing down the ACA env + the three
   container apps, the three UAMIs, ACR, Log Analytics, Application Insights, and Key Vault.

2. **Manual principal cleanup** (the single reviewed manual step, SC-010) — the `uami-api` / `uami-mcp`
   `pgaadauth` Postgres principals live in the ledger DB, NOT in this stack's state, so `tofu destroy`
   cannot remove them. As the Entra admin (owner), connect to the ledger from inside the VNet and drop
   them + revoke their grants (the inverse of the bootstrap `pgaadauth_create_principal_with_oid` +
   `GRANT` emitted by the `pgaadauth_bootstrap_uami_{api,mcp}` outputs):

   ```sql
   -- Connect as the Entra admin (owner) to the ledger:
   --   psql "host=<ledger-fqdn> dbname=<db> user=<owner-upn> sslmode=require"
   -- DROP OWNED also revokes every GRANT the principal held on ipam/registry.
   REASSIGN OWNED BY "uami-pdp-westus3-api" TO "<owner-upn>";
   DROP OWNED BY "uami-pdp-westus3-api";
   DROP ROLE "uami-pdp-westus3-api";

   REASSIGN OWNED BY "uami-pdp-westus3-mcp" TO "<owner-upn>";
   DROP OWNED BY "uami-pdp-westus3-mcp";
   DROP ROLE "uami-pdp-westus3-mcp";
   ```

3. **Verify zero residue** (SC-010) — via Azure Resource Graph, confirm no spec-7 resource remains and
   the ledger RG is untouched; confirm no dangling role assignments (the subscription `Reader` grants on
   `uami-api`/`uami-mcp` are deleted with the UAMIs) and no orphaned Entra registrations.

The subscription-level `Reader` role assignments are scoped to the UAMI principals and are removed
automatically when `tofu destroy` deletes the UAMIs. The ledger RG is never in scope.

> Spec 007 status: **US1 + US2 + US4 resources authored** — RG, 3 UAMIs, ACR, Key Vault, Log Analytics,
> Application Insights (workspace-based), ACA env, and the ingress/api/mcp container apps (with
> `APPLICATIONINSIGHTS_CONNECTION_STRING` wired onto api/mcp). `tofu fmt`/`init -backend=false`/`validate`
> all green under OpenTofu 1.11.6. Live apply + the Postgres principal bootstrap + telemetry verification
> (T050) run in dispatched CI against Azure.
