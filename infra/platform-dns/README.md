# Platform-Shared DNS Stack

Owns the **platform-wide, region-agnostic set of global Azure Private DNS zones**
(`privatelink.*`) that PDP private endpoints resolve against. State key: **`platform/dns`**
in the PDP backend (spec-001 foundations). Rides the spec-001 plan-on-PR / apply-on-merge
rails (OIDC, zero stored secrets). Created once; long-lived.

| Field | Value |
|---|---|
| State backend | `rg-pdp-eastus2-foundations` / `stpdpeus2stateokoq` / `tfstate` |
| State key | `platform/dns` |
| Region | East US 2 (RG location only; the zones are **global**) |

## Ownership rule — zones live here, links live in fabrics

Azure Private DNS zones are **global** resources. This unit owns the **zones only**. The
**hub VNet → zone links** are created by the **fabric stack** (`infra/fabric`, spec 003), and
**spoke VNet → zone links** by **spoke vending** (spec 004). This split is deliberate
(research §3, clarify Q3):

- A single global zone set avoids the per-region duplicate / split-horizon zones that owning
  zones in the fabric stack would cause (a second region's fabric would collide on the same
  global zone names — FR-016).
- Fabric teardown removes only the hub *links*; these shared zones **survive** (mirroring how
  the ledger hub carve-out survives — FR-014). Consumers look the zones up by name via
  `data "azurerm_private_dns_zone"`, keeping the fabric loosely coupled to this unit.

So: **no `virtual_network_links` are declared in this stack.** Adding one here would be a bug.

## Resources (CAF-named, `pdp-*` tagged — Article III)

| Resource | Name |
|---|---|
| Resource group | `rg-pdp-eastus2-dns` |
| Private DNS zone | `privatelink.postgres.database.azure.com` (Postgres Flexible Server private endpoints) |
| Private DNS zone | `privatelink.blob.core.windows.net` (Storage / blob) |
| Private DNS zone | `privatelink.vaultcore.azure.net` (Key Vault) |

The RG carries the **universal tags only** (`pdp-managed`, `pdp-deployed-by`) — **no
`pdp-fabric`**: this unit is region-agnostic and is not a fabric (data-model.md §4). Zone names
are the DNS domains themselves (`docs/conventions.md` §1.3 private-DNS exception).

The `postgres` zone here (`privatelink.postgres.database.azure.com`, for private endpoints) is
**distinct** from the control-plane stack's VNet-integrated
`pdp-controlplane.private.postgres.database.azure.com` zone — a different zone form; they do
not conflict (research §3).

**Growing the set:** add new `privatelink.*` zones by PR as services arrive. ACA's
region-qualified zone (`privatelink.<region>.azurecontainerapps.io`) is **deferred to spec 008**
(it is region-qualified, not guessed now).

## Outputs

| Output | Type | Purpose |
|---|---|---|
| `dns_resource_group_name` | `string` | RG holding the global zones — for inventory / handoff. |
| `shared_dns_zone_ids` | `map(string)` | Zone (domain) name → resource ID. |

The fabric does **not** consume these outputs — it `data`-looks-up the zones by name in this RG
(loose coupling). The outputs exist for visibility and parity with the fabric's downstream
`shared_dns_zone_ids` contract (contracts/fabric-interfaces.md §I2).

## AVM module adoption (Article V) — smoke validation

Smoke-tested under **OpenTofu 1.11.6** (the `.opentofu-version` pin), 2026-06-15:

| Module | Version (pinned exact) | Result |
|---|---|---|
| `Azure/avm-res-network-privatednszone/azurerm` | `0.5.0` | ✅ init + validate + plan |

- **init + validate**: clean — the module input shape (`domain_name`, `parent_id`,
  `enable_telemetry`, `tags`; **no** `virtual_network_links`) and the `resource_id` output
  reference resolve.
- **plan** (local backend override, against live ARM — read-only, no real state touched):
  **7 to add, 0 to change, 0 to destroy** = 1 RG + 3 zones + 3 module-internal `time_sleep`
  helpers; outputs resolve (`dns_resource_group_name = "rg-pdp-eastus2-dns"`, the 3-key
  `shared_dns_zone_ids` map). Apply lands via CI from the first apply (`iac-plan` on the PR is
  the authoritative plan; `iac-apply` on merge applies it) — no local apply.
- The module uses an azapi implementation; harmless `multiplier` attribute-deprecation warnings
  surface from inside the module (azapi `retry` block) — no action needed (same warning the
  control-plane stack documents).

The RG is a plain `azurerm` primitive — no AVM composition exists for a single resource group
(justification per Article V).

## CI

- `iac-plan` (PR): `fmt`-check repo-wide, then `validate` + `plan` for the `platform-dns` stack
  (matrix entry alongside `foundations` / `control-plane`); plan rendered on the PR.
- `iac-apply` (push to `main`): re-plan + apply, serialized in its concurrency group.

No dedicated destroy workflow: these global zones are long-lived platform infrastructure (not a
per-region fabric). They are intentionally **not** protected by `prevent_destroy` — they hold no
record-of-record and re-create cleanly; the fabric depends on their *existence*, not their
contents.
