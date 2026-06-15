# Contract: Control-Plane Database (infrastructure)

The IaC contract for the control-plane Postgres deployment (`infra/control-plane/`, state key
`platform/control-plane`). The IPAM ledger is its first schema; spec 006 adds the environment
registry, provisioning runs, and archetype catalog to the **same** database. Rides the
spec-001 plan-on-PR / apply-on-merge rails (OIDC, no stored secrets).

## Resources (CAF-named, `pdp-*` tagged — Article III)

| Resource | Name | Notes |
|---|---|---|
| Resource group | `rg-pdp-eastus2-controlplane` | Universal tags (`pdp-managed`, `pdp-deployed-by`); no scope tags (platform-plane). |
| Virtual network | `vnet-pdp-eastus2-controlplane` | Address space **`10.0.0.0/24`** (platform supernet reservation, data-model §1/§5). |
| Delegated subnet | `snet-pdp-eastus2-cp-postgres` | **`10.0.0.0/28`**, delegated to `Microsoft.DBforPostgreSQL/flexibleServers`. |
| Private DNS zone | `…private.postgres.database.azure.com` | Name ends in `postgres.database.azure.com`, ≠ server name; vnet-linked. |
| Postgres Flexible Server | `psql-pdp-eastus2-controlplane` | Config below. |

## Server configuration (from research)

| Setting | Value | Requirement |
|---|---|---|
| Networking | VNet-injected; `public_network_access_enabled = false` | FR-002 (no public endpoint) |
| Auth | `active_directory_auth_enabled = true`, `password_auth_enabled = false`; owner as Entra admin | FR-003 (zero stored secrets) |
| Extensions | `azure.extensions = "BTREE_GIST"` (server configuration) | FR-006 (GiST exclusion) |
| Backup | `backup_retention_days = 30`, `geo_redundant_backup_enabled = false` | FR-014 (30-day PITR), Article IX (LRS) |
| SKU / storage | `sku_name = "B_Standard_B1ms"`, `storage_mb = 32768` | Article IX (smallest viable) |
| Version | a current PG major supporting `btree_gist` (e.g. 16/17) | FR-006 |

Modules: `Azure/avm-res-dbforpostgresql-flexibleserver/azurerm` (pin exact, smoke-test under
OpenTofu 1.11.6 — Article V), `Azure/avm-res-network-virtualnetwork/azurerm`, private DNS via
AVM or azurerm primitive (record justification if hand-rolled).

## Protection & teardown (Articles IV, VIII)

- The database holds the **allocation record of record**. Teardown MUST require explicit typed
  confirmation via a `controlplane-destroy` `workflow_dispatch` (mirrors
  `foundations-destroy`), and the stack documents the consequence (loss of the live ledger;
  the schema re-applies from migrations, but recorded allocations are lost) — FR-015.
- Consider a `CanNotDelete` management lock on the RG and/or `prevent_destroy` on the server,
  removable only via reviewed PR (the same Article IV carve-out pattern spec-001 used for the
  state backend). The protected set is enumerated in `infra/control-plane/README.md`.
- All other resources (VNet, subnet, DNS zone/link) tear down cleanly.

## Identity / migration handoff (research §13)

- This stack deploys the **empty** server with `BTREE_GIST` allow-listed. The IPAM schema +
  seed are **authored and tested here** (Testcontainers) but **applied to the live DB in spec
  006**, when an in-VNet runtime exists to connect (the DB is private + Entra-only; CI cannot
  reach it). The Entra admin is the owner now; the control-plane managed identity is added in
  spec 006.

## State key

`platform/control-plane` in the PDP backend (registry from spec-001
`contracts/state-backend.md`). `use_azuread_auth = true`; CI uses OIDC.
