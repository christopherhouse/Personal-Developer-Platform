# Spoke stack — pinned toolchain + the TWO providers a cross-subscription vend needs.
# A spoke lands in an arbitrary writable TARGET subscription, but its hub-side peering, its
# spoke→shared-zone DNS links, and the fabric remote-state read all live in the PLATFORM
# subscription. So the default provider authenticates into the target sub and an aliased
# "platform" provider into the platform sub; resources pick their provider explicitly
# (data-model §3, contracts §I3). Both authenticate via OIDC in dispatched CI (Articles I/II).
terraform {
  required_version = "~> 1.11.0"

  required_providers {
    azurerm = {
      source  = "hashicorp/azurerm"
      version = "~> 4.77.0"
    }
    # Transitive requirements of the AVM virtualnetwork module (matches the fabric stack pins).
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

# Default provider — the TARGET subscription. Owns the spoke RG, VNet/subnets, NSGs, route
# table, and the spoke→hub peering.
provider "azurerm" {
  subscription_id = var.target_subscription_id

  # Entra-only data plane (matches every other PDP stack): the state account has shared-key
  # access disabled, so storage data operations against the tfstate container use Entra ID.
  storage_use_azuread = true

  features {}
}

# Aliased provider — the PLATFORM subscription (hosts the hub + shared DNS zones + fabric
# state). Owns the hub→spoke peering on the hub VNet and the spoke→shared-zone DNS links, and
# backs the terraform_remote_state read against fabrics/<region>.
provider "azurerm" {
  alias           = "platform"
  subscription_id = var.platform_subscription_id

  storage_use_azuread = true

  features {}
}
