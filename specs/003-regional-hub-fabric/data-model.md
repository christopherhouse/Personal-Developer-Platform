# Data Model: Regional Hub Fabric

This feature has **no database schema** — its "entities" are Azure resources in two OpenTofu
stacks plus their typed inputs/outputs. Names follow `docs/conventions.md`; CIDRs derive from
the IPAM ledger's hub carve-out (research §4–§5). East US 2 (`region_index = 1`) is shown
concretely; every value is region-parameterized (FR-016).

## §1 Deployable units (state keys)

| Unit | Dir | State key | Scope | Lifecycle |
|---|---|---|---|---|
| **Regional hub fabric** | `infra/fabric` | `fabrics/<region>` (`fabrics/eastus2`) | One region | Deploy/destroy per region |
| **Platform-shared DNS** | `infra/platform-dns` | `platform/dns` | Platform-wide, region-agnostic | Created once; long-lived |

Both ride the spec-001 backend (account `stpdpeus2stateokoq`, RG `rg-pdp-eastus2-foundations`,
container `tfstate`, `use_azuread_auth = true`).

## §2 Fabric stack inputs (variables)

| Variable | Type | Example (eastus2) | Notes |
|---|---|---|---|
| `region` | `string` | `eastus2` | Full Azure region name; drives names, `pdp-fabric` tag, location. |
| `region_index` | `number` (1–255) | `1` | 2nd octet of the region `/16`; **the only address knob** (research §4). The registered region's index — sourced from `register_region`; passed by the control plane (spec 006) or set directly until then. |
| `platform_subscription_id` | `string` | `8bd05b2f-…` | Platform subscription (default as in control-plane stack). |
| `platform_dns_resource_group_name` | `string` | `rg-pdp-eastus2-dns` | Where the shared zones live; the fabric `data`-looks-up zones here to link the hub. |

**Derived locals** (not inputs):
- `hub_address_space = "10.${region_index}.252.0/22"` — the ledger hub carve-out.
- `region_short` — from conventions §1.2 (`eastus2`→`eus2`); only for constrained names.
- `tags` — `{ pdp-managed="true", pdp-deployed-by="github-actions", pdp-fabric=region }`.

## §3 Fabric stack resources

CIDRs via `cidrsubnet(local.hub_address_space, 4, n)` (research §5).

| # | Resource | Name (eastus2) | Key properties |
|---|---|---|---|
| 1 | `azurerm_resource_group` | `rg-pdp-eastus2-fabric` | `tags` (incl. `pdp-fabric=eastus2`); `lifecycle.prevent_destroy = true` (FR-015). |
| 2 | `azurerm_management_lock` | `lock-pdp-eastus2-fabric` | `CanNotDelete` on the RG (Article IV/VIII gate, research §7). |
| 3 | `module.vnet` (`virtualnetwork` 0.18.0) | `vnet-pdp-eastus2-hub` | `address_space=["10.1.252.0/22"]`; subnets below. |
| 3a | subnet | `AzureFirewallSubnet` | `10.1.252.0/26`; no NSG, no route table. |
| 3b | subnet | `AzureFirewallManagementSubnet` | `10.1.252.64/26`; no NSG/UDR. |
| 3c | subnet | `AzureBastionSubnet` | `10.1.252.128/26`; no route table; NSG omitted. |
| 4 | `module.pip_fw_data` (`publicipaddress` 0.2.1) | `pip-pdp-eastus2-afw` | Standard / Static. |
| 5 | `module.pip_fw_mgmt` | `pip-pdp-eastus2-afw-mgmt` | Standard / Static (Basic-SKU management NIC, research §1). |
| 6 | `module.pip_bastion` | `pip-pdp-eastus2-bas` | Standard / Static. |
| 7 | `module.fw_policy` (`firewallpolicy` 0.3.4) | `afwp-pdp-eastus2-hub` | `firewall_policy_sku="Basic"`. |
| 8 | `module.firewall` (`azurefirewall` 0.4.0) | `afw-pdp-eastus2-hub` | `firewall_sku_tier="Basic"`, `firewall_sku_name="AZFW_VNet"`, `firewall_policy_id=#7`, `ip_configurations`=data PIP+`AzureFirewallSubnet`, `firewall_management_ip_configuration`=mgmt PIP+`AzureFirewallManagementSubnet`. |
| 9 | `module.bastion` (`bastionhost` 0.9.0) | `bas-pdp-eastus2-hub` | `sku="Basic"`, `ip_configuration{subnet=AzureBastionSubnet, create_public_ip=false, public_ip_address_id=#6}`. |
| 10 | `azurerm_private_dns_zone_virtual_network_link` ×N | `vnetlink-pdp-eastus2-hub-<zone>` | One per shared zone (#§4); zone IDs from `data` sources in the `platform-dns` RG; `registration_enabled=false`. |

**Not created here** (deliberate, Article IX): any route table / `0.0.0.0/0` UDR (→ spec 004,
consumes output `firewall_private_ip`); any spoke peering (→ spec 004); any NAT gateway.

## §4 Platform-DNS stack resources

| # | Resource | Name | Notes |
|---|---|---|---|
| 1 | `azurerm_resource_group` | `rg-pdp-eastus2-dns` | Universal tags only (platform scope, **no** `pdp-fabric`); holds the global zones (location = primary region; zones themselves are global). |
| 2 | `module.zone_postgres` (`privatednszone` 0.5.0) | `privatelink.postgres.database.azure.com` | Postgres Flexible Server private-endpoint zone (≠ control-plane's VNet-integrated zone). |
| 3 | `module.zone_blob` | `privatelink.blob.core.windows.net` | Storage/blob. |
| 4 | `module.zone_kv` | `privatelink.vaultcore.azure.net` | Key Vault. |

The fabric **links** the hub VNet to these (does not own them). Grow the set by PR as services
arrive (research §3); ACA's region-qualified zone deferred to spec 008.

## §5 Fabric stack outputs (the fabric's downstream contract)

Consumed by spoke vending (spec 004) and the control plane (spec 006). See
`contracts/fabric-interfaces.md`.

| Output | Type | Example | Consumer / purpose |
|---|---|---|---|
| `hub_vnet_id` | `string` | `/subscriptions/…/vnet-pdp-eastus2-hub` | Spoke ⇄ hub peering target (spec 004). |
| `hub_vnet_name` | `string` | `vnet-pdp-eastus2-hub` | Peering / display. |
| `firewall_private_ip` | `string` | `10.1.252.4` | **The single egress next-hop** — spoke `0.0.0.0/0` UDR (spec 004, Article VII). |
| `hub_resource_group_name` | `string` | `rg-pdp-eastus2-fabric` | Cross-sub peering & inventory. |
| `hub_address_space` | `string` | `10.1.252.0/22` | The ledger carve-out actually deployed (SC-002 trace). |
| `shared_dns_zone_ids` | `map(string)` | `{postgres=…, blob=…, kv=…}` | Spoke-side DNS links (spec 004). |

## §6 Lifecycle

```
register_region(eastus2,index=1)        ── ledger reserves 10.1.252.0/22 (spec 002; prerequisite)
platform-dns apply (once)               ── shared privatelink.* zones exist
fabric apply (region=eastus2,index=1)   ── RG+lock, hub VNet+subnets, 3 PIPs, fw policy,
                                            firewall(Basic+mgmt NIC), bastion(Basic),
                                            hub→zone links ─▶ outputs (firewall_private_ip, …)
fabric destroy (fabric-destroy wf)      ── typed-confirm → remove lock → destroy RG;
                                            carve-out (ledger) + shared zones SURVIVE
```

State transitions are OpenTofu apply/destroy convergence — re-apply is idempotent (no
duplicate hub). The carve-out is a standing reservation (no `allocation` row created/released),
so teardown leaks no address space (FR-003).

## §7 Validation rules (enforced in the stack)

- `region_index` ∈ `[1, 255]` (index 0 is the platform supernet — spec 002 §1); `var
  validation` block.
- Reserved subnet names exact: `AzureFirewallSubnet`, `AzureFirewallManagementSubnet`,
  `AzureBastionSubnet` (Azure-mandated).
- Firewall PIPs `sku="Standard"` (Basic firewall rejects Basic PIPs — research §1/§6).
- No NSG/route table on the three reserved subnets (Azure rejects otherwise — research §5).
- `firewall_policy_sku="Basic"` paired with `firewall_sku_tier="Basic"` (mismatch fails).
- The `/22` must hold the three `/26`s — structurally guaranteed (16 `/26`s available).
