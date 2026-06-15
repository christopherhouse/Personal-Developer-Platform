# Identifiers the spec-006 runtime binds to when it connects in-VNet and applies the IPAM
# migrations (research §13). The server is private + Entra-only, so these are names/ids only
# — never a connection secret (FR-003).

output "server_name" {
  description = "Name of the control-plane Postgres Flexible Server."
  value       = module.postgres.name
}

output "server_fqdn" {
  description = "Private FQDN of the control-plane Postgres (resolves via the linked private DNS zone)."
  value       = module.postgres.fqdn
}

output "resource_group_name" {
  description = "Resource group holding the control-plane stack."
  value       = azurerm_resource_group.control_plane.name
}

output "vnet_id" {
  description = "Resource ID of the control-plane VNet (spec 006 vnet-integrates the ACA control plane here)."
  value       = module.vnet.resource_id
}

output "postgres_subnet_id" {
  description = "Resource ID of the delegated Postgres subnet."
  value       = module.vnet.subnets["postgres"].resource_id
}
