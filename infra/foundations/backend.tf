# Seed backend — the owner's pre-existing personal state account (external dependency).
# Holds exactly one PDP state: this stack's. Never managed, tagged, or remediated by PDP
# (contracts/state-backend.md). Verified Entra-only (shared keys disabled) 2026-06-11.
terraform {
  backend "azurerm" {
    subscription_id      = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
    resource_group_name  = "RG-TF"
    storage_account_name = "cmhtfstatesa"
    container_name       = "tfstate"
    key                  = "pdp/foundations"
    use_azuread_auth     = true
  }
}
