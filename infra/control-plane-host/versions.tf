terraform {
  required_version = "~> 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.77.0"
    }
    # The AVM Container Apps modules (managedenvironment / containerapp) wrap azapi.
    azapi = {
      source  = "Azure/azapi"
      version = "~> 2.7"
    }
    # Transitive requirements of the AVM resource modules.
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

provider "azurerm" {
  subscription_id = var.platform_subscription_id

  # Entra-only data plane (matches every PDP stack): the state account has shared-key access
  # disabled, so the provider uses Entra ID for storage data operations against the tfstate container.
  storage_use_azuread = true

  features {}
}

provider "azapi" {}
