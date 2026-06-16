# Platform-shared DNS outputs — surface the RG and the global zone IDs for inventory and for
# any consumer that reads this stack's state. NOTE: the fabric (infra/fabric) links the hub by
# looking the zones up via `data "azurerm_private_dns_zone"` in this RG (loose coupling,
# research §3) — it does not depend on these outputs; they exist for visibility and parity with
# the fabric's downstream `shared_dns_zone_ids` contract.

output "dns_resource_group_name" {
  description = "Resource group holding the platform-shared global Private DNS zones."
  value       = azurerm_resource_group.dns.name
}

output "shared_dns_zone_ids" {
  description = "Map of zone (domain) name → Private DNS zone resource ID. Grows by PR as services arrive."
  value = {
    "privatelink.postgres.database.azure.com" = module.zone_postgres.resource_id
    "privatelink.blob.core.windows.net"       = module.zone_blob.resource_id
    "privatelink.vaultcore.azure.net"         = module.zone_kv.resource_id
  }
}
