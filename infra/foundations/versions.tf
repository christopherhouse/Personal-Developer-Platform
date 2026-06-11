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

  # Entra-only data plane: both state accounts have shared-key access disabled, so the
  # provider must use Entra ID for storage data operations (e.g., creating the tfstate
  # container). Without this, container operations attempt key-based auth and fail.
  storage_use_azuread = true

  features {}
}
