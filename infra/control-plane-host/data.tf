# Consumed-by-reference resources from the existing platform stacks. This stack CREATES none of these —
# it reads them so the host footprint stays isolated and the ledger RG is never in this stack's scope
# (FR-016, SC-010). Implemented in T010 (Foundational); a rename upstream fails clearly here at plan time.

# The ACA delegated subnet (snet-pdp-westus3-aca, 10.0.0.32/27, delegation Microsoft.App/environments),
# DECLARED by the VNet-owning infra/control-plane stack (T008) — consumed by name + VNet rather than via
# terraform_remote_state (looser coupling, no upstream state access; research §10). Its id feeds the ACA
# managed environment's infrastructure_subnet_id (T018).
data "azurerm_subnet" "aca" {
  name                 = var.aca_subnet_name
  virtual_network_name = var.control_plane_vnet_name
  resource_group_name  = var.control_plane_resource_group_name
}

# The private, Entra-only Postgres flexible server (created by infra/control-plane). We read it for its
# private FQDN only — the api/mcp apps build a password-less, Entra-token connection to it (research §5).
# Same VNet as the ACA subnet, so the existing privatelink.postgres DNS link resolves it (research §3) —
# no new link is created here.
data "azurerm_postgresql_flexible_server" "ledger" {
  name                = var.postgres_server_name
  resource_group_name = var.control_plane_resource_group_name
}

# The platform-shared private DNS RG (owned by infra/platform-dns). Referenced for context only: the
# privatelink.postgres.database.azure.com zone is already linked to the control-plane VNet, so the
# same-VNet ACA apps resolve Postgres through it (research §3) — this stack adds no zone and no link.
data "azurerm_resource_group" "platform_dns" {
  name = var.platform_dns_resource_group_name
}
