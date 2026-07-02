# Consumed-by-reference resources. This archetype CREATES no network fabric (FR-012): the spoke's
# shared ACA environment + subnets arrive via its remote state, and the shared observability / DNS /
# ACR resources are looked up by name in the platform subscription. A missing spoke state (spoke not
# vended on the spec-008 stack yet) fails the plan fast, before any workload resource is evaluated.

data "azurerm_client_config" "current" {}

# The containing spoke's published contract (infra/spoke outputs): the shared ACA environment the
# container app runs on, and the `workload` subnet the SQL private endpoint lands in. Same state
# account as this module's own backend; Entra auth; platform sub pinned so the read resolves
# regardless of the target sub the default provider points at.
data "terraform_remote_state" "spoke" {
  backend = "azurerm"

  config = {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    key                  = "spokes/${var.target_subscription_id}/${var.spoke_name}"
    use_azuread_auth     = true
    subscription_id      = var.platform_subscription_id
  }
}

# The platform-shared Log Analytics workspace (infra/platform-observability) — SQL server/database
# diagnostics ship here (Article XI; the ACA environment's logs are wired by the spoke stack).
data "azurerm_log_analytics_workspace" "platform" {
  provider = azurerm.platform

  name                = var.observability_workspace_name
  resource_group_name = var.observability_resource_group_name
}

# The shared privatelink.database.windows.net zone (infra/platform-dns) — the SQL private
# endpoint's zone group registers here; the spoke's VNet is already linked (spec-004 mechanism).
data "azurerm_private_dns_zone" "sql" {
  provider = azurerm.platform

  name                = "privatelink.database.windows.net"
  resource_group_name = var.platform_dns_resource_group_name
}

# The platform ACR — looked up ONLY when the image reference targets it (conditional AcrPull;
# any other reference must be publicly resolvable and needs no registry credential).
data "azurerm_container_registry" "platform" {
  provider = azurerm.platform

  count = local.uses_platform_acr ? 1 : 0

  name                = var.platform_acr_name
  resource_group_name = var.platform_acr_resource_group_name
}
