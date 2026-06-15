# Regional Hub Fabric Stack

Stands up a complete regional network **hub** for a registered Azure region: the hub VNet (from
the ledger carve-out), a single **Azure Firewall (Basic)** as the region's controlled egress, an
**Azure Bastion (Basic)** for private management, and the **hub → platform-shared Private DNS**
links. One `tofu apply` per region; region-parameterized (FR-016). State key: **`fabrics/<region>`**
(first region `fabrics/eastus2`) in the PDP backend (spec-001 foundations). Rides the spec-001
plan-on-PR / apply-on-merge rails (OIDC, zero stored secrets).

| Field | Value |
|---|---|
| State backend | `rg-pdp-eastus2-foundations` / `stpdpeus2stateokoq` / `tfstate` |
| State key | `fabrics/eastus2` (per-region; set at init/dispatch — spec 009 ergonomics) |
| Region inputs | `region` (e.g. `eastus2`) + `region_index` (1–255) — **the only two knobs** |

## Article VI — address space is *consumed*, not invented

The hub VNet address space is the region's **ledger hub carve-out**
`10.<region_index>.252.0/22` — the top `/22` of the region's `/16`, a **standing reservation**
in `region_pool.hub_carveout` (spec 002 data-model §1/§2). The fabric **mirrors** it
deterministically from the registered `region_index` (locals.tf); it does **not** invent address
space and creates **no** `allocation` row — so teardown leaks nothing (FR-003). The `/22` is
carved by `cidrsubnet(hub, 4, n)` into the three reserved Azure subnets (`AzureFirewallSubnet`
n0, `AzureFirewallManagementSubnet` n1, `AzureBastionSubnet` n2), each a `/26`, with 13 `/26`s of
headroom. The **live** "is this region registered?" check (FR-004) is the **control plane's**
(spec 006) — it queries the private, Entra-only ledger and only then dispatches this workflow.
At the IaC layer the registered `region_index` is a typed input (as all PDP stacks trust their
typed inputs).

> ⚠️ No NSG and no route table are attached to the three reserved subnets — Azure rejects them
> there. The `0.0.0.0/0 → firewall_private_ip` UDR, spoke peering, and NSGs are **spec 004**, not
> here. The fabric only exposes the next-hop.

## Multi-region (FR-016) — change only `region` + `region_index`

A second registered region stands up from **this same code** by changing only the two inputs —
no module/code edits. Everything addressable and named flows from `var.region` /
`var.region_index` / `local.*`; the audit (T021) confirms no hardcoded region/index/CIDR leaks in
any resource body. Worked example (verified offline via `tofu console`, T022):

| | `region=eastus2, region_index=1` | `region=westus3, region_index=2` |
|---|---|---|
| Hub address space | `10.1.252.0/22` | `10.2.252.0/22` |
| Reserved `/26`s | `…252.0` / `…252.64` / `…252.128` | same offsets in `10.2.252.x` |
| Names | `…-pdp-eastus2-…` | `…-pdp-westus3-…` |
| `pdp-fabric` tag | `eastus2` | `westus3` |

Two things are **deliberately not** derived from `var.region`:

- **The state backend key** (`backend.tf`: `fabrics/eastus2`). OpenTofu backends can't take
  variables, so the per-region key is set at **init / dispatch** (e.g.
  `tofu init -backend-config="key=fabrics/westus3"`). The backend RG/account are the single
  region-agnostic spec-001 backend. Full multi-region dispatch ergonomics (a region matrix /
  per-region keys wired into CI) are **spec 009** — this stack only proves the parameterization.
- **`platform_dns_resource_group_name`** (default `rg-pdp-eastus2-dns`). The platform-shared DNS
  zones are **global and created once** (`infra/platform-dns`); *every* region's hub links to the
  **same** RG, so this input is intentionally region-agnostic, not derived from `var.region`.

> Prerequisite (deploy-time, not code): the target region must be **registered in the ledger**
> (`register_region`, giving it its `region_index`) before a meaningful apply — the control plane
> (spec 006) enforces this pre-dispatch; the IaC trusts its typed `region_index` (Article VI).

## Egress — Azure Firewall **Basic**: two Standard PIPs + a mandatory management NIC

The region's single controlled egress (Article VII) is an Azure Firewall **Basic**
(`firewall_sku_tier = "Basic"`, `firewall_sku_name = "AZFW_VNet"`) with a Basic-tier policy.
**Basic SKU mandates a management NIC**, so the firewall carries **two** configs:

- a **data** IP config — `AzureFirewallSubnet` + `pip-pdp-<region>-afw`, and
- a **management** IP config — `AzureFirewallManagementSubnet` + `pip-pdp-<region>-afw-mgmt`.

Both PIPs are **Standard / Static** — Azure Firewall **rejects Basic PIPs** regardless of the
firewall tier (research §1/§6). The firewall's **private IP** is the single default-route
next-hop spokes point at, exposed as the `firewall_private_ip` output. The current map input
`ip_configurations` is used (the list-form `firewall_ip_configuration` is deprecated; the module
rejects mixing them).

## Management — Azure Bastion **Basic** (private only)

An Azure Bastion **Basic** in the hub's `AzureBastionSubnet` with a Standard/Static PIP
(`pip-pdp-<region>-bas`), `create_public_ip = false` (the platform-named PIP is attached
explicitly). Basic is the **floor SKU that supports VNet peering**, so this single hub bastion
reaches VMs in **peered spokes** — the management model the fabric exists for. The Developer SKU
was rejected (no peering — research §2). There is **no public management endpoint** otherwise
(Article IX); the bastion PIP is platform-required, not a workload endpoint.

## Private DNS — links here, zones in `infra/platform-dns`

The platform-shared global `privatelink.*` zones are **owned by `infra/platform-dns`** (state key
`platform/dns`). This stack creates only the **hub VNet → zone links**, looking the zones up by
name via `data "azurerm_private_dns_zone"` in `var.platform_dns_resource_group_name` (loose
coupling — no remote state). `registration_enabled = false`. So fabric teardown removes only the
links; the shared zones **survive** (FR-006 / FR-014). The link map keys (`postgres` / `blob` /
`kv`) are the stable keys of the `shared_dns_zone_ids` output.

## Cross-subscription peering readiness (hub side)

The fabric exposes `hub_vnet_id` and `hub_resource_group_name` so **spoke vending (spec 004)** can
create the **hub side** of cross-subscription spoke ⇄ hub peering. The fabric itself creates **no**
peerings — spokes own their side and live in their own state. (Hub-to-hub connectivity is spec
009.)

## Outputs (downstream contract — contracts/fabric-interfaces.md §I2)

| Output | Type | Consumer / purpose |
|---|---|---|
| `hub_vnet_id` | `string` | Hub side of spoke peering (spec 004). |
| `hub_vnet_name` | `string` | Peering / display. |
| `hub_resource_group_name` | `string` | Locate the hub for peering & inventory. |
| `hub_address_space` | `string` | The ledger carve-out deployed (SC-002 trace). |
| `firewall_private_ip` | `string` | **The single egress next-hop** (Article VII) — read live, never hardcoded. |
| `shared_dns_zone_ids` | `map(string)` | Spoke-side DNS links at vending (spec 004). |

## AVM module adoption (Article V) — smoke validation

Smoke-validated under **OpenTofu 1.11.6** (the `.opentofu-version` pin), 2026-06-15:

| Module | Version (pinned exact) | Result |
|---|---|---|
| `Azure/avm-res-network-virtualnetwork/azurerm` | `0.18.0` | ✅ init + validate |
| `Azure/avm-res-network-publicipaddress/azurerm` | `0.2.1` | ✅ init + validate |
| `Azure/avm-res-network-firewallpolicy/azurerm` | `0.3.4` | ✅ init + validate |
| `Azure/avm-res-network-azurefirewall/azurerm` | `0.4.0` | ✅ init + validate |
| `Azure/avm-res-network-bastionhost/azurerm` | `0.9.0` | ✅ init + validate |

- **init + validate**: clean — every module input shape resolves at the pinned versions:
  - VNet `subnets` map (reserved Azure names, no NSG/route table) + `module.vnet.subnets[k].resource_id`.
  - Firewall current-input **`ip_configurations`** (map) — the module's "exactly one subnet_id"
    validation passes with the single data config; `firewall_management_ip_configuration` (object:
    `name`+`subnet_id`+`public_ip_address_id`) supplies the mandatory Basic management NIC; private
    IP read from the `module.firewall.resource` output.
  - Public IP `sku`/`allocation_method` and `module.pip_*.public_ip_id`.
  - Bastion takes **`parent_id`** (not `resource_group_name`) + the `ip_configuration` object with
    `create_public_ip = false` and an explicit `public_ip_address_id` — the module validations for
    the Basic SKU (ip_configuration required, PIP-id required when not creating one) pass.
  - `azurerm_private_dns_zone_virtual_network_link` against `data`-source zone IDs.
- **plan**: the full `tofu plan` runs in **CI** (`iac-plan` on the PR). The three
  `data.azurerm_private_dns_zone` lookups resolve only once `infra/platform-dns` has been applied
  (the shared zones must exist), so a clean local plan is not run from a laptop — `iac-plan` is the
  authoritative plan, `iac-apply` on merge applies it. No local apply (Article I/II).
- The bastion/firewall modules use azapi internally; harmless `multiplier` attribute-deprecation
  warnings may surface from inside the azapi `retry` blocks — no action needed (same warning the
  control-plane and platform-dns stacks document).

The resource group is a plain `azurerm` primitive — no AVM composition exists for a single
resource group (justification per Article V).

## Protected resources (Article IV/VIII carve-out — FR-015)

A hub destroy severs this region's **egress + management** and would orphan spoke peerings —
high blast radius, so the fabric is **destroyable-by-design but guarded**:

| Resource | Protection |
|---|---|
| `rg-pdp-eastus2-fabric` | `prevent_destroy` lifecycle **and** `CanNotDelete` management lock (`lock-pdp-eastus2-fabric`) |
| firewall / bastion / VNet / PIPs / DNS links | covered by the RG management lock (inherited) + the RG `prevent_destroy` guard — no dedicated per-resource guard |

**Why the guard is on the RG, not each resource:** the firewall and bastion are provisioned by
AVM modules, so a `lifecycle { prevent_destroy }` block cannot be injected onto those resources
directly (the same module-internal limitation the control-plane stack documents). The RG
`prevent_destroy` makes any `tofu destroy` **fail at plan time**, and the `CanNotDelete` lock
blocks deletes from every plane (portal / CLI / IaC) — together the equivalent Article-IV
carve-out.

## Teardown handoff — the `fabric-destroy` workflow (SC-006/007, Scenario 7)

Deliberate teardown is a single confirmed workflow, never automatic (Article VIII):

1. **Protection-removal PR (reviewed):** delete the `azurerm_management_lock.fabric` resource and
   the RG `prevent_destroy` flag from `main.tf`; plan + merge. The apply removes the lock.
2. **Run `fabric-destroy`** (`workflow_dispatch`, `.github/workflows/fabric-destroy.yml`) and type
   the region **`eastus2`** to confirm. It `tofu init` → `plan -destroy` (the SC-007 drill: this
   step **fails** if step 1 hasn't merged) → `destroy`.

What **survives** (correctly): the ledger **hub carve-out** `10.1.252.0/22` (a standing
reservation in `region_pool` — no `allocation` row was ever created, FR-003) and the
**platform-shared DNS zones** (owned by `infra/platform-dns` — only the hub *links* are removed,
FR-014). A re-apply reuses the identical `/22` (SC-007 — no address drift).

**Scope boundary:** this spec's fabric owns **no** spoke peerings (spokes own their side, in their
own state — spec 004/006), so the destroy plan surfaces no hub-side peerings. Peering-aware
teardown ("destroy with live peerings") is a **spec 004/006** concern, not covered here.

## CI

- `iac-plan` (PR): `fmt`-check repo-wide, then `validate` + `plan` for the `fabric` stack (matrix
  entry alongside `foundations` / `control-plane` / `platform-dns`); plan rendered on the PR.
- `iac-apply` (push to `main`): re-plan + apply, serialized in its concurrency group.
- `fabric-destroy` (`workflow_dispatch`): the gated teardown above — typed region confirm,
  concurrency group `tofu-fabric` (shares the apply lock).
