terraform {
  required_version = "~> 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.77.0"
    }
  }
}

provider "azurerm" {
  subscription_id = var.platform_subscription_id

  # Entra-only data plane (matches the foundations stack): the state account has
  # shared-key access disabled, so the provider must use Entra ID for storage data
  # operations against the tfstate container.
  storage_use_azuread = true

  features {}
}
