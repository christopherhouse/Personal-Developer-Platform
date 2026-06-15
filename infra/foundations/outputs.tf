# PDP state backend identifiers — the values every later stack's backend block and the
# CI variables consume (contracts/state-backend.md).

output "state_resource_group_name" {
  description = "Resource group holding the PDP state backend."
  value       = azurerm_resource_group.foundations.name
}

output "state_storage_account_name" {
  description = "PDP state storage account (generated constrained name)."
  value       = module.state_storage.name
}

output "state_container_name" {
  description = "Blob container holding all PDP unit states."
  value       = azurerm_storage_container.tfstate.name
}

# output "ci_client_id" {
#   description = "Client ID of the GitHub CI managed identity (plain value, not a secret)."
#   value       = azurerm_user_assigned_identity.github_ci.client_id
# }
