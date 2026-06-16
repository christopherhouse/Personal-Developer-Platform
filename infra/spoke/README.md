# Spoke Stack

Vends **one spoke** into an arbitrary writable **target** Azure subscription: a spoke resource
group, a configurable-shape VNet (subnets + per-subnet NSGs + an egress route table), **both**
sides of cross-subscription spoke ⇄ hub peering, and **spoke → platform-shared Private DNS** links.
One `spoke-vend` dispatch per spoke; fully input-parameterized (FR-010). The spoke is the
disposable, high-churn unit — **no lock, no `prevent_destroy`** — and is torn down by the
confirm-gated `spoke-destroy` workflow.

| Field | Value |
|---|---|
| State backend | `rg-pdp-westus3-foundations` / `stpdpwus3statejqyq` / `tfstate` (the spec-001 platform-sub backend) |
| State key | **`spokes/<target-sub-id>/<spoke-name>`** — per spoke, set at init via partial backend config |
| Identity (state) | platform sub (where the state account lives) via OIDC |
| Worked example | `region=westus3`, `region_index=2`, `spoke_name=app1`, `spoke_cidr=10.2.16.0/24` |

## Typed-CIDR staging (Gate G1 — live allocation is spec 006)

`spoke_cidr` is a **typed input**, not a live ledger allocation. The stack validates that the block
lies **inside the region pool `10.<region_index>.0.0/16`** and **outside the hub carve-out
`10.<region_index>.252.0/22`** (`variables.tf`, deterministic `cidr*` checks), but it performs **no**
by-size allocation and writes **no** ledger row.

**Why:** the IPAM ledger is private, Entra-only Postgres injected in the control-plane VNet;
OpenTofu runs on GitHub-hosted runners that cannot reach it, and the constitution places allocation
in the control plane (Plan Gate G1, 2026-06-16). Live by-size allocation + ledger write/release are
the **spec-006 control plane's** job. Until then the owner supplies a `spoke_cidr` recorded
out-of-band, and **cross-spoke non-overlap is operator-checked** — the stack only enforces region
containment + hub-carve-out avoidance. (Same staging spec 002's seeded reservations and spec 003's
typed `region_index` used — a documented Article-VI refinement.)

## Dual-subscription identity (contracts §I3)

A vend authenticates via OIDC into **two** subscriptions through **two `azurerm` providers**
(`versions.tf`):

| Provider | Subscription | Owns |
|---|---|---|
| default | `var.target_subscription_id` | spoke RG, VNet/subnets, NSGs, route table, **spoke→hub** peering |
| `azurerm.platform` (alias) | `var.platform_subscription_id` | **hub→spoke** peering on the hub VNet, **spoke→shared-zone** DNS links, the fabric remote-state read |

OIDC is per-app, not per-sub, so **one** federated credential authenticates both — provided the CI
identity holds the roles in each: **Contributor** in the target sub, **Network Contributor** on the
hub RG + **Private DNS Zone Contributor** on the DNS RG in the platform sub. The federated
credential must also cover the `spoke-vend` / `spoke-destroy` dispatch refs (the branch-dispatch
fed-cred gap surfaced during the westus3 migration — verified in T008).

## Upstream — the fabric, read live (contracts §I1)

`data.terraform_remote_state.fabric` reads `fabrics/<region>` (platform sub) for `hub_vnet_id`,
`hub_resource_group_name`, `firewall_private_ip`, and `shared_dns_zone_ids` (`locals.tf`). Nothing
about the hub is hardcoded (FR-010); a region whose fabric has not been applied has no state blob,
so the read **fails the plan fast** (FR-011) before any spoke resource is evaluated. The hub VNet
**name** and each DNS zone's **RG + name** are derived from those IDs in `locals.tf`.

## Egress + NSG posture (Articles VII / IX)

- **Egress (SC-004):** one `azurerm_route_table` with a single `0.0.0.0/0 → VirtualAppliance`
  next-hop = `firewall_private_ip`, associated to **every** spoke subnet. No alternative default
  route, no NAT — the hub firewall is the spoke's only egress. Both peerings set
  `allow_forwarded_traffic = true` so the firewall can forward spoke traffic.
- **NSG (FR-005/018):** one `azurerm_network_security_group` per subnet, **Azure default rules
  only** (no custom rules), associated to every subnet via the AVM module. Archetypes (spec 008)
  add workload-specific rules later; the UDR — not the NSG — forces traffic through the hub.

## Configurable shape (FR-017)

`var.subnets` is a `map(object({ newbits, netnum, delegations }))`. Each subnet's prefix is
`cidrsubnet(spoke_cidr, newbits, netnum)`; `delegations` (a list of service names) passes straight
through to the AVM module. Default: a single `workload` subnet filling the whole block
(`newbits=0, netnum=0`). The `spoke-vend` workflow accepts an optional `subnets_json` to override.

## Identity & idempotency (FR-007/009)

`(target subscription, spoke-name)` is the spoke identity, realized by the
`spokes/<sub-id>/<spoke-name>` state key. Re-vending the same `(sub, name)` targets the same state
and **converges — no duplicate**; a new name adds an independent spoke in the same subscription.

## Outputs (downstream contract → spec 008 workloads, contracts §I4)

| Output | Type | Purpose |
|---|---|---|
| `spoke_vnet_id` | `string` | Workload subnet placement / further integration. |
| `spoke_resource_group_name` | `string` | Where workloads land; inventory. |
| `spoke_subnets` | `map(string)` | Subnet logical name → resource ID for workload placement. |
| `spoke_cidr` | `string` | The deployed block (SC-002 trace). |

## AVM module adoption (Article V) — smoke validation

Smoke-validated under **OpenTofu 1.11.6** (the `.opentofu-version` pin), 2026-06-16:

| Module | Version (pinned exact) | Result |
|---|---|---|
| `Azure/avm-res-network-virtualnetwork/azurerm` | `0.18.0` | ✅ init (`-backend=false`) + validate |

- **init + validate**: clean. The `subnets` map resolves with per-subnet
  `network_security_group = { id }`, `route_table = { id }`, and
  `delegations = [{ name, service_delegation = { name } }]` (verified against the v0.18.0 schema);
  `module.vnet.resource_id` / `module.vnet.name` / `module.vnet.subnets[k].resource_id` back the
  peerings and outputs.
- The RG, NSGs, route table, peerings, and DNS links are plain `azurerm` primitives — no AVM
  composition exists for a single one of these, so they are hand-rolled (justification per
  Article V). The same VNet module the hub uses covers VNet + subnets + NSG-attach + route-attach.
- **plan/apply**: the full `tofu plan`/`apply` runs only in **dispatched CI** (`spoke-vend`), which
  can reach the fabric remote state and authenticate into both subscriptions. No local apply
  (Articles I/II). The AVM module pulls `azapi` internally — harmless `multiplier`
  attribute-deprecation warnings may surface from its `retry` blocks (same as the other stacks).

## Teardown — no lock, confirm-gated (Articles IV / VIII)

Spokes carry **no** `prevent_destroy` and **no** management lock (research §7) — locks caused the
migration's lock-vs-operation traps, and spokes are the disposable unit. `spoke-destroy`
(`workflow_dispatch`) requires a typed `destroy-confirm` equal to the spoke name, then
`tofu init` (the spoke's key) → `destroy`. The destroy removes **both** peering sides — the
hub-side `peer-hub-to-<name>` via the aliased platform provider — so **no dangling hub peering**
remains (Article IV). Sibling spokes, the hub, and the shared DNS zones are untouched; the
`spoke_cidr` block becomes reusable.

## CI

- `spoke-vend` (`workflow_dispatch`): inputs `region`, `region_index`, `target_subscription_id`,
  `spoke_name`, `spoke_cidr`, optional `subnets_json`. OIDC into both subs → `init` (per-spoke
  backend key) → `plan` → `apply`. Concurrency group `tofu-spoke-<name>` (a vend never races a
  destroy of the same spoke).
- `spoke-destroy` (`workflow_dispatch`): typed `destroy-confirm` = spoke name; same dual-sub OIDC
  and concurrency group.
