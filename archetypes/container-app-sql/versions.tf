# container-app-sql archetype — pinned toolchain + the TWO providers a workload deploy needs.
# The workload lands in the spoke's TARGET subscription (default provider); the shared Log
# Analytics workspace, the privatelink.database.windows.net zone, the platform ACR (conditional
# AcrPull), and the spoke remote-state read live in the PLATFORM subscription (aliased provider).
# Both authenticate via OIDC in the dispatched workload-deploy/destroy workflows (Articles I/II).
terraform {
  required_version = "~> 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.77.0"
    }
    # Transitive requirements of the AVM modules (matches the platform stack pins).
    random = {
      source  = "hashicorp/random"
      version = "~> 3.7"
    }
    time = {
      source  = "hashicorp/time"
      version = "~> 0.13"
    }
    modtm = {
      source  = "Azure/modtm"
      version = "~> 0.3"
    }
  }
}

# Default provider — the TARGET subscription (the spoke's). Owns the workload RG, UAMI, container
# app, SQL server + database, and the private endpoint.
provider "azurerm" {
  subscription_id = var.target_subscription_id

  # Entra-only data plane (matches every other PDP stack): the state account has shared-key
  # access disabled, so storage data operations against the tfstate container use Entra ID.
  storage_use_azuread = true

  features {}
}

# Aliased provider — the PLATFORM subscription: shared observability + DNS zone + ACR lookups, the
# conditional AcrPull grant, and the spoke remote-state read.
provider "azurerm" {
  alias           = "platform"
  subscription_id = var.platform_subscription_id

  storage_use_azuread = true

  features {}
}
