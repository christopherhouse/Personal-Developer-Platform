# container-app-sql outputs — the workload's published facts (contracts/execution-plane.md).

output "workload_resource_group_name" {
  description = "Resource group holding the workload — the destroy unit; inventory."
  value       = azurerm_resource_group.workload.name
}

output "container_app_fqdn" {
  description = "The app's URL — internal (environment-private) by default, public only when parameters.publicEndpoint opted in."
  value       = module.container_app.fqdn_url
}

output "sql_server_fqdn" {
  description = "The SQL server's FQDN — resolves to the private endpoint via the shared privatelink.database.windows.net zone (spoke-linked); unreachable from outside the fabric."
  value       = local.sql_server_fqdn
}

output "container_app_principal_id" {
  description = "The workload UAMI's principal id (the app's identity AND the SQL Entra admin — R6)."
  value       = azurerm_user_assigned_identity.workload.principal_id
}
