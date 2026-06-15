# Control-Plane Stack

Deploys the **private, Entra-only control-plane Postgres** that hosts the IPAM ledger
(spec 002, this stack's first schema) and, from spec 006, the rest of the control-plane
database (environment registry, provisioning runs, archetype catalog). State key:
**`platform/control-plane`** in the PDP backend (spec-001 foundations). Rides the spec-001
plan-on-PR / apply-on-merge rails (OIDC, zero stored secrets).

| Field | Value |
|---|---|
| State backend | `rg-pdp-eastus2-foundations` / `stpdpeus2stateokoq` / `tfstate` |
| State key | `platform/control-plane` |
| Region | East US 2 (`var` defaults; the control-plane DB is singular, primary region) |

## Resources (CAF-named, `pdp-*` tagged — Article III)

| Resource | Name |
|---|---|
| Resource group | `rg-pdp-eastus2-controlplane` |
| Virtual network | `vnet-pdp-eastus2-controlplane` (`10.0.0.0/24`) |
| Delegated subnet | `snet-pdp-eastus2-cp-postgres` (`10.0.0.0/28`, delegated to `Microsoft.DBforPostgreSQL/flexibleServers`) |
| Private DNS zone | `pdp-controlplane.private.postgres.database.azure.com` (+ vnet-link) |
| Postgres Flexible Server | `psql-pdp-eastus2-controlplane` |
| Management lock | `lock-pdp-eastus2-controlplane` (`CanNotDelete`) |

The VNet range is **not invented here**: `10.0.0.0/24` is the seeded platform-supernet
reservation recorded by the IPAM migration (data-model.md §1/§5, research §12) — Article VI
holds the moment the schema exists.

### Server posture (confirmed by smoke plan — SC-006)

`public_network_access_enabled = false` (no public endpoint) · `active_directory_auth_enabled
= true` + `password_auth_enabled = false` (Entra-only; **no `administrator_login`/password =
zero stored secrets**) · owner as Entra admin (`principal_type = User`) · `backup_retention_days
= 30`, `geo_redundant_backup_enabled = false` (LRS) · `sku_name = B_Standard_B1ms`, `storage_mb
= 32768`, PG `16` · `azure.extensions = BTREE_GIST` (backs the GiST EXCLUDE non-overlap
constraint, FR-006).

## AVM module adoption (Article V) — smoke validation

Smoke-tested under **OpenTofu 1.11.6** (the `.opentofu-version` pin), 2026-06-15:

| Module | Version (pinned exact) | Result |
|---|---|---|
| `Azure/avm-res-dbforpostgresql-flexibleserver/azurerm` | `0.2.2` | ✅ init + validate + plan |
| `Azure/avm-res-network-virtualnetwork/azurerm` | `0.18.0` | ✅ init + validate + plan |
| `Azure/avm-res-network-privatednszone/azurerm` | `0.5.0` | ✅ init + validate + plan |

- **init + validate**: clean — all module input shapes (`authentication`, `ad_administrator`,
  `server_configuration`, `delegated_subnet_id`, `private_dns_zone_id`, subnet `delegations`,
  `virtual_network_links`) and output references resolve.
- **plan** (local backend, against live ARM — read-only, no real state touched): **10 to add,
  0 change, 0 destroy**; names + posture as above. Apply/destroy in a scratch RG was **not**
  run locally — unlike the foundations bootstrap, this stack rides CI from its first apply
  (`iac-plan` on the PR is the authoritative plan; `iac-apply` on merge lands it).
- ⚠️ **CRITICAL FINDING — public allow-all firewall default.** The Postgres module's
  `firewall_rules` input **defaults** to `AllowAllFireWallRule` (`0.0.0.0`–`255.255.255.255`),
  a public allow-all that violates FR-002 / Article IX. `main.tf` **explicitly overrides it to
  `firewall_rules = {}`** (a VNet-injected server takes no firewall rules). Do not remove this
  override. The smoke plan confirms zero firewall resources are created.
- The private DNS zone uses the AVM module's azapi implementation; harmless `multiplier`
  attribute-deprecation warnings surface from inside the module (azapi `retry` block) — no
  action needed.

The RG and management lock are plain `azurerm` primitives — no AVM composition exists for
single resources (justification per Article V).

## Protected resources (Article IV carve-out — FR-015)

This database holds the **live allocation record of record**. Destroying it loses every
recorded allocation (the schema re-applies from migrations; the *data* does not).

| Resource | Protection |
|---|---|
| `rg-pdp-eastus2-controlplane` | `prevent_destroy` lifecycle **and** `CanNotDelete` management lock |
| `psql-pdp-eastus2-controlplane` | covered by the RG management lock (inherited) + the RG `prevent_destroy` guard |
| `vnet` / `subnet` / DNS zone + link | no dedicated guard — tear down cleanly once stack protection is removed |

**Why the guard is on the RG, not the server directly:** the server is provisioned by an AVM
module, so a `lifecycle { prevent_destroy }` block cannot be injected onto the server resource
(the same module-internal limitation the foundations stack documents). The RG `prevent_destroy`
makes any full-stack `tofu destroy` **fail at plan time**, and the `CanNotDelete` lock blocks
deletes from every plane (portal/CLI/IaC) — together the equivalent Article-IV carve-out.

**Teardown drill (SC-007):** running `controlplane-destroy` with protection in place →
`tofu plan -destroy` fails on the RG `prevent_destroy` (and the lock blocks ARM deletes).
**Removing protection** = a reviewed PR that deletes the `azurerm_management_lock.control_plane`
resource and the RG `prevent_destroy` flag, planned and merged; then the owner runs
`controlplane-destroy` and types `control-plane` to confirm (Article VIII).

## Migration / identity handoff (research §13)

This stack deploys the **empty** server with `BTREE_GIST` allow-listed. The IPAM schema + seed
are **authored and tested here** (Testcontainers, spec 002) but **applied to the live DB in
spec 006**, when an in-VNet runtime exists to connect — the server is private + Entra-only, so
CI cannot (and should not) reach it. The owner is the Entra admin now (`var.owner_object_id` /
`var.owner_principal_name`); the **control-plane managed identity is added as an Entra principal
in spec 006**. Confirm `var.owner_principal_name` matches the directory UPN for the owner object
ID before the first apply (it is the public email by default, which may differ from the UPN).

## CI

- `iac-plan` (PR): `fmt`-check repo-wide, then `validate` + `plan` for the `control-plane` stack
  (matrix entry alongside `foundations`); plan rendered on the PR. Required status check.
- `iac-apply` (push to `main`): re-plan + apply, serialized in concurrency group
  `tofu-control-plane`; failed-apply guard blocks push-triggered re-runs (FR-016).
- `controlplane-destroy` (`workflow_dispatch`): typed `control-plane` confirmation, shares the
  `tofu-control-plane` lock (Article VIII).
