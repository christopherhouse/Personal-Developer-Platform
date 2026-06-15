# Regional hub fabric outputs — the fabric's downstream contract (contracts/fabric-interfaces.md
# §I2), read by spoke vending (spec 004) and the control plane (spec 006). Stable names; values
# are read live by consumers, never hardcoded (the firewall private IP in particular may change
# if the firewall is re-allocated).

output "hub_vnet_id" {
  description = "Resource ID of the hub VNet — the hub side of cross-subscription spoke peering (spec 004)."
  value       = module.vnet.resource_id
}

output "hub_vnet_name" {
  description = "Name of the hub VNet — for peering and display."
  value       = module.vnet.name
}

output "hub_resource_group_name" {
  description = "Resource group holding the hub — locate it for peering and inventory."
  value       = azurerm_resource_group.fabric.name
}

output "hub_address_space" {
  description = "The ledger hub carve-out actually deployed (10.<region_index>.252.0/22) — for inventory / overlap sanity (SC-002 trace)."
  value       = local.hub_address_space
}

output "firewall_private_ip" {
  description = "The single egress next-hop (Article VII): spokes point their 0.0.0.0/0 UDR at this IP (spec 004). Read live — may change if the firewall is re-allocated."
  value       = module.firewall.resource.ip_configuration[0].private_ip_address
}

output "shared_dns_zone_ids" {
  description = "Map of short zone key (postgres/blob/kv) → shared Private DNS zone resource ID — for spoke-side DNS links at vending (spec 004). The zones are owned by infra/platform-dns; the fabric only links the hub to them."
  value       = { for k, z in data.azurerm_private_dns_zone.shared : k => z.id }
}
