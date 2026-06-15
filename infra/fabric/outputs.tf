# Regional hub fabric outputs — the fabric's downstream contract (contracts/fabric-interfaces.md
# §I2), read by spoke vending (spec 004) and the control plane (spec 006). Implemented in T014
# (Phase 3 / US1) once the hub resources exist:
#   - hub_vnet_id              : string       — spoke ⇄ hub peering target
#   - hub_vnet_name            : string       — peering / display
#   - hub_resource_group_name  : string       — locate the hub for peering & inventory
#   - hub_address_space         : string       — the ledger carve-out actually deployed (SC-002)
#   - firewall_private_ip       : string       — the single egress next-hop (Article VII)
#   - shared_dns_zone_ids       : map(string)   — spoke-side DNS links
#
# Stub only at Phase 1 (scaffolding): no resources exist yet, so there is nothing to export.
# `tofu validate` is green with zero output blocks.
