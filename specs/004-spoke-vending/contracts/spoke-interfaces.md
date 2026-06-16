# Contract: Spoke Interfaces (upstream fabric/ledger, downstream workloads)

The spoke stack's typed boundary with the rest of the platform.

## I1 — Upstream: fabric (spec 003) → spoke

Read via `terraform_remote_state` against `fabrics/<region>` (spec-003 §I2 outputs). The spoke
**consumes**; it never mutates the hub except to add the hub-side peering.

| Item | Source | Contract |
|---|---|---|
| `hub_vnet_id` | fabric output | Remote VNet ID for the spoke→hub peering and the hub→spoke peering target. |
| `hub_resource_group_name` | fabric output | Locates the hub RG (platform sub) for the hub-side peering. |
| `firewall_private_ip` | fabric output | The `0.0.0.0/0` next-hop in the spoke route table (Article VII). Read live; never hardcoded. |
| `shared_dns_zone_ids` | fabric output (map) | One spoke→zone link per entry; keys (`postgres`/`blob`/`kv`) grow by PR. |

**Precondition**: the region's fabric MUST be applied (the remote-state read fails fast otherwise →
FR-011).

## I2 — Upstream: IPAM ledger (spec 002) → spoke

| Item | Source | Contract |
|---|---|---|
| Region `/16` | `region_pool` (spec 002) | The spoke block MUST lie within `10.<region_index>.0.0/16` and outside the hub carve-out `…252.0/22`. |
| Spoke allocation row | — | **Not created by this spec.** The live by-size allocation + ledger write/release are the **spec-006 control plane's** (Plan Gate G1). Until then the `spoke_cidr` input is trusted and recorded out-of-band; cross-spoke non-overlap becomes ledger-enforced when 006 lands. |

## I3 — Identity / plane boundary

A vend authenticates via OIDC into **two** subscriptions: the **target** subscription (spoke RG,
VNet, spoke-side peering, route table, NSGs) and the **platform** subscription (hub-side peering on
the hub VNet, spoke→shared-zone DNS links, fabric remote-state read). The deploy identity needs
Contributor in the target sub and at least Network Contributor on the hub RG + Private DNS Zone
Contributor on the DNS RG in the platform sub. OpenTofu runs only in dispatched CI (Articles I/II).

## I4 — Downstream: spoke → workloads (spec 008)

Exposed as outputs (data-model §4): `spoke_vnet_id`, `spoke_resource_group_name`, `spoke_subnets`,
`spoke_cidr`. Workload archetypes (spec 008) deploy into `spoke_subnets`; this spec guarantees the
spoke is peered, egressing via the hub, NSG-protected, and DNS-linked.

## I5 — Egress posture guarantee (Article VII)

Every spoke routes `0.0.0.0/0` to the hub firewall private IP and defines **no** alternative
egress. Spokes never peer to each other. This is the structural backing of SC-004.
