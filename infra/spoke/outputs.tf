# Spoke outputs — the downstream contract consumed by workloads (spec 008, contracts §I4).
# Resource-backed as of T016 (main.tf creates the RG/VNet/subnets). Workload archetypes deploy
# into spoke_subnets; this stack guarantees the spoke is peered, hub-egressing, NSG'd, DNS-linked.

output "spoke_vnet_id" {
  description = "Resource ID of the spoke VNet — workload subnet placement / further integration (spec 008)."
  value       = module.vnet.resource_id
}

output "spoke_resource_group_name" {
  description = "Resource group holding the spoke — where workloads land; inventory."
  value       = azurerm_resource_group.spoke.name
}

output "spoke_subnets" {
  description = "Map of subnet logical name → subnet resource ID, for workload placement (spec 008)."
  value       = { for k, s in module.vnet.subnets : k => s.resource_id }
}

output "spoke_cidr" {
  description = "The spoke address block actually deployed (SC-002 trace) — echoes the validated input."
  value       = var.spoke_cidr
}

output "spoke_aca_environment_id" {
  description = "Resource ID of the spoke's shared ACA managed environment (spec 008, R5) — workload archetypes deploy their container apps onto it by ID (cross-RG reference from the workload's own state)."
  value       = module.aca_environment.resource_id
}
