# Data Model: Spoke Vending

No database schema. "Entities" are the spoke OpenTofu stack's typed inputs, the Azure resources it
creates, and its outputs. Names follow `docs/conventions.md`; the spoke CIDR is a typed input
within the region's `/16` (research §1). `westus3` (`region_index = 2`, `/16` = `10.2.0.0/16`) is
the worked example.

## §1 Deployable unit

| Unit | Dir | State key | Scope | Lifecycle |
|---|---|---|---|---|
| **Spoke** | `infra/spoke` | `spokes/<sub-id>/<spoke-name>` | One spoke in one target subscription | Vend / destroy per spoke; no lock |

Parameterized (not a singleton): the same code vends every spoke; the backend key and the target
provider are set at init/dispatch.

## §2 Inputs (variables)

| Variable | Type | Example | Notes |
|---|---|---|---|
| `region` | `string` | `westus3` | Drives names, location, `pdp-*` tags; must have a deployed fabric. |
| `region_index` | `number` (1–255) | `2` | The region `/16` is `10.<region_index>.0.0/16`; bounds the `spoke_cidr` validation. |
| `target_subscription_id` | `string` | `<sub-guid>` | Where the spoke deploys; **must be writable**; may differ from the platform sub. |
| `platform_subscription_id` | `string` | `8bd05b2f-…` | Hosts the hub (hub-side peering, fabric remote state); aliased provider. |
| `spoke_name` | `string` | `app1` | Unique within the target subscription; part of the identity + state key. |
| `spoke_cidr` | `string` (CIDR) | `10.2.16.0/24` | **Typed allocation** (research §1). `validation`: MUST be inside `10.<region_index>.0.0/16` and outside the hub carve-out `…252.0/22`. |
| `subnets` | `map(object)` | `{ workload = { newbits=0, delegations=[] } }` | Configurable shape: per-subnet size + delegations (FR-017). Default: one workload subnet filling the block. |

**Derived locals**: `region` short form (conventions); spoke subnet prefixes via
`cidrsubnet(var.spoke_cidr, …)`; `tags = { pdp-managed, pdp-deployed-by, pdp-spoke=spoke_name }`.

## §3 Resources

CIDRs via `cidrsubnet(var.spoke_cidr, …)`. Built primarily with
`Azure/avm-res-network-virtualnetwork/azurerm`.

| # | Resource | Name (example) | Key properties |
|---|---|---|---|
| 1 | `azurerm_resource_group` (target sub) | `rg-pdp-westus3-spoke-app1` | `tags` incl. `pdp-spoke=app1`; **no** `prevent_destroy`, **no** lock. |
| 2 | `module.vnet` (virtualnetwork) | `vnet-pdp-westus3-app1` | `address_space=[var.spoke_cidr]`; `subnets` from `var.subnets`, each with an NSG + the egress route table associated. |
| 2a | spoke subnet(s) | `snet-pdp-westus3-app1-workload` | size/delegations from input; NSG + route-table attached. |
| 3 | `azurerm_network_security_group` ×N (or shared) | `nsg-pdp-westus3-app1-<subnet>` | **Azure default rules only** (FR-018); attached to every subnet. |
| 4 | `azurerm_route_table` | `rt-pdp-westus3-app1` | `0.0.0.0/0` → `VirtualAppliance` next-hop `firewall_private_ip`; associated to spoke subnets. |
| 5 | `azurerm_virtual_network_peering` (spoke→hub, target sub) | `peer-app1-to-hub` | `remote_virtual_network_id=hub_vnet_id`; `allow_forwarded_traffic=true`. |
| 6 | `azurerm_virtual_network_peering` (hub→spoke, platform sub, **aliased provider**) | `peer-hub-to-app1` | on the hub VNet; `allow_forwarded_traffic=true`. |
| 7 | `azurerm_private_dns_zone_virtual_network_link` ×N (platform sub) | `vnetlink-pdp-westus3-app1-<zone>` | one per shared zone in `shared_dns_zone_ids`; `registration_enabled=false`. |

**Upstream (read, not created)**: fabric outputs via `terraform_remote_state["fabrics/<region>"]`
— `hub_vnet_id`, `hub_resource_group_name`, `firewall_private_ip`, `shared_dns_zone_ids`.

**Not created here**: any live IPAM ledger row (spec 006); workloads inside the spoke (spec 008);
any NAT/alternative egress (Article VII).

## §4 Outputs (downstream contract → spec 008 workloads)

| Output | Type | Purpose |
|---|---|---|
| `spoke_vnet_id` | `string` | Workload subnet placement / further integration. |
| `spoke_resource_group_name` | `string` | Where workloads land; inventory. |
| `spoke_subnets` | `map(string)` | Subnet name → resource ID for workload placement. |
| `spoke_cidr` | `string` | The deployed block (SC-002 trace). |

## §5 Validation rules (enforced in the stack)

- `spoke_cidr` MUST be within `10.<region_index>.0.0/16` and MUST NOT overlap the hub carve-out
  `10.<region_index>.252.0/22` (`var validation` + `cidr*` checks). *(Cross-spoke non-overlap is
  the ledger's job once spec 006 records allocations; within this spec the typed block is trusted.)*
- `spoke_name` unique within the subscription (enforced by the `spokes/<sub-id>/<spoke-name>` state
  key — a duplicate name targets the same state and converges, FR-009).
- Every subnet has an NSG association (FR-005) and the egress route table (FR-004).
- Region MUST have a deployed fabric (the `terraform_remote_state` read fails clearly otherwise).

## §6 Lifecycle

```
(spec 006 later: allocate by-size + record in ledger) ── for now: owner supplies spoke_cidr
spoke-vend (region, sub, name, cidr, shape) ── RG + VNet/subnets(+NSG+route) in target sub,
                                                both peering sides, DNS links ─▶ outputs
spoke-destroy (typed-confirm = spoke name)  ── removes the spoke incl. BOTH peering sides;
                                                hub/zones/sibling spokes untouched
```

Re-vend converges (no duplicate). Teardown leaves no dangling hub-side peering (removed via the
aliased platform provider).
