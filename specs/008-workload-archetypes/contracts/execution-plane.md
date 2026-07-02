# Contract — Execution Plane: Workflows, State, Archetype Module

## `workload-deploy.yml` (modeled on `spoke-vend.yml`)

- `run-name: pdp ${{ inputs.mode }} ${{ inputs.env_id }}` (env_id correlation — the
  existing `RunNameCorrelation` parses it; webhook + reconciler unchanged).
- Inputs: `env_id`, `mode` (choice `plan`|`apply`, default `apply`), `region`,
  `target_subscription_id`, `spoke_name`, `workload_name`, `archetype_path`,
  `archetype_ref`, `parameters_json`, `pdp_env`.
- `permissions: contents: read, id-token: write`; OIDC `azure/login@v3` from repo
  variables (`AZURE_CLIENT_ID/TENANT_ID/SUBSCRIPTION_ID`), `ARM_USE_OIDC=true`,
  `ARM_USE_AZUREAD=true`.
- `concurrency: tofu-workload-${{ inputs.spoke_name }}-${{ inputs.workload_name }}`
  (shared group with `workload-destroy.yml`).
- **Pinned-tag checkout**: `actions/checkout` with `ref: ${{ inputs.archetype_ref }}`
  (R7). Steps then run `tofu -chdir=${{ inputs.archetype_path }}`.
- Init: `tofu init -backend-config="key=workloads/${{ inputs.target_subscription_id }}/${{ inputs.spoke_name }}/${{ inputs.workload_name }}"`
  (FR-011; same partial-backend mechanism/storage account as every stack).
- Vars threaded as `TF_VAR_*`; `parameters_json` passed whole
  (`TF_VAR_parameters=${{ inputs.parameters_json }}`, module variable
  `parameters` of object type with defaults).
- Plan branch uploads `plan-${{ inputs.env_id }}` artifact (`plan.txt` + `plan.json`)
  — the control plane fills `PlanResult.PlanSummary` from it exactly as for spokes.
- Trailing `notify-sre-agent` job on failure (repo convention).

## `workload-destroy.yml` (modeled on `spoke-destroy.yml`)

Same shape; `mode` choice `plan`|`destroy`; required input `destroy-confirm`; gate step
fails the run when `inputs.destroy-confirm != inputs.workload_name` (second layer under
`ConfirmationGuard`). Checks out `archetype_ref` = the **stamped** version (verb
supplies it from `registry.workloads`) so destroy semantics match what was applied.
`tofu plan -destroy` then (destroy mode) `tofu destroy -auto-approve`.

## `reusable-container-build.yml` (first `workflow_call` workflow)

```yaml
on:
  workflow_call:
    inputs:
      image_repository: { required: true,  type: string }   # e.g. workloads/demo-api
      dockerfile:       { required: true,  type: string }
      context:          { required: false, type: string, default: "." }
```

Steps: OIDC `azure/login@v3` (caller repo's `AZURE_*` variables) → `az acr login`
(token, no stored secret) → `docker build` tagged `:$GITHUB_SHA` + `:latest` →
push both. Extracted from the proven `controlplane-host-images.yml` pattern.
Stamped repos call it:
`uses: <owner>/Personal-Developer-Platform/.github/workflows/reusable-container-build.yml@main`.
Caller prerequisites (template README, one-time per stamped repo — R8): federated
credential `repo:<owner>/<repo>:ref:refs/heads/main` on the CI app registration,
`AcrPush` on the platform ACR, the three `AZURE_*` repo variables.

## Archetype module contract: `archetypes/container-app-sql/`

**Inputs** (all supplied by the workflow, never by the caller directly):
`region`, `target_subscription_id`, `platform_subscription_id`, `spoke_name`,
`workload_name`, `pdp_env`, `parameters` (object — the schema-validated payload),
plus observability names (workspace/RG, control-plane-host convention).

**Consumes by reference** (creates NO network fabric — FR-012):
- Spoke remote state (`key=spokes/<sub>/<spoke>`): `spoke_aca_environment_id`,
  `spoke_subnets["workload"]` (private-endpoint subnet), `spoke_resource_group_name`.
- Shared Log Analytics workspace + `privatelink.database.windows.net` zone by
  data source (platform subscription).

**Creates** (in the target subscription):
| Resource | Module / resource | Notes |
|---|---|---|
| RG `rg-pdp-<region>-workload-<name>` | `azurerm_resource_group` | carries the full tag set |
| UAMI `uami-pdp-<region>-wl-<name>` | `azurerm_user_assigned_identity` | app identity; SQL Entra admin; conditional `AcrPull` on platform ACR when `containerImage` targets it (README-justified raw resources, spec-007 precedent) |
| Container app `ca-pdp-<region>-<name>` | AVM `avm-res-app-containerapp` 0.9.0 | on the **spoke's shared env** (cross-RG by ID); `ingress.external = parameters.publicEndpoint` (default false); env vars: SQL connection string (`Authentication=Active Directory Managed Identity`), no secrets |
| SQL server `sql-pdp-<region>-<name>` | AVM `avm-res-sql-server` 0.2.1 | Entra-only auth, public access disabled, PE into spoke `workload` subnet + DNS zone group; diagnostics → shared LA |
| SQL db `sqldb-<workload_name>` | (same module, `databases`) | `GP_S_Gen5_1`, `min_capacity 0.5`, `auto_pause_delay_in_minutes = parameters.sqlAutoPauseDelayMinutes`, `max_size_gb = parameters.sqlMaxSizeGb` |

**Tags** (workload RG + taggable resources — FR-013):
`pdp-managed=true`, `pdp-deployed-by=github-actions`,
`pdp-workload=<workload_name>`, `pdp-env=<pdp_env>`.

**Outputs**: `workload_resource_group_name`, `container_app_fqdn` (internal or public),
`sql_server_fqdn` (privatelink), `container_app_principal_id`.

## Cross-spec stack edits (recorded)

| Stack | Change | Why |
|---|---|---|
| `infra/spoke` | default `subnets` = `workload` + `aca` (`/27`, delegated `Microsoft.App/environments`); NEW shared ACA managed environment `cae-pdp-<region>-<spoke>` (AVM managedenvironment 0.4.0, workload-profiles, consumption only, external, VNet-integrated, diagnostics → shared LA); outputs `+ spoke_aca_environment_id` | R5 — subnets belong to the VNet-owning stack (spec-007 precedent); env required for UDR/hub egress (Article VII) |
| `infra/platform-dns` | + `privatelink.database.windows.net` (AVM privatednszone 0.5.0) | R6 — SQL private endpoint resolution |
| `infra/fabric` | `shared_dns_zone_ids` += `sql` key | spokes auto-link every published zone (spec-004 mechanism) |
| `.github/workflows/controlplane-host-images.yml` | paths += `archetypes/catalog.json` | catalog changes trigger image rebuild → startup sync (R1) |
| `infra/fabric` firewall policy | contingency only: ACA platform FQDN application rules if live acceptance shows blocks | R10 |
