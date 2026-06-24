# Surface the RG + workspace for inventory and for any consumer that reads this stack's state. Consumers
# attach diagnostics by looking the workspace up via `data "azurerm_log_analytics_workspace"` in this RG
# (loose coupling, mirrors how the fabric resolves the shared DNS zones) — they do not depend on these
# outputs; they exist for visibility and parity.

output "observability_resource_group_name" {
  description = "Resource group holding the platform-shared Log Analytics workspace."
  value       = azurerm_resource_group.observability.name
}

output "log_analytics_workspace_id" {
  description = "Resource ID of the platform-shared Log Analytics workspace — the single 'all resources -> Log Analytics' sink."
  value       = module.log_analytics.resource_id
}

output "log_analytics_workspace_name" {
  description = "Name of the platform-shared Log Analytics workspace (consumers data-source it by this name)."
  value       = local.law_name
}
