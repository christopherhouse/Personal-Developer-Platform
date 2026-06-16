# Spoke outputs — the downstream contract consumed by workloads (spec 008, contracts §I4).
# STUBS for Phase 1: only spoke_cidr (a pure echo of the validated input) is wired now; the
# resource-backed values are implemented in T016 once main.tf creates the RG/VNet/subnets.
# Stubbed as null/{} so `tofu validate` is green on the empty stack (no dangling references).

output "spoke_vnet_id" {
  description = "Resource ID of the spoke VNet — workload subnet placement / further integration (spec 008)."
  value       = null # TODO(T016): module.vnet.resource_id
}

output "spoke_resource_group_name" {
  description = "Resource group holding the spoke — where workloads land; inventory."
  value       = null # TODO(T016): azurerm_resource_group.spoke.name
}

output "spoke_subnets" {
  description = "Map of subnet logical name → subnet resource ID, for workload placement (spec 008)."
  value       = {} # TODO(T016): { for k, s in module.vnet.subnets : k => s.resource_id }
}

output "spoke_cidr" {
  description = "The spoke address block actually deployed (SC-002 trace) — echoes the validated input."
  value       = var.spoke_cidr
}
