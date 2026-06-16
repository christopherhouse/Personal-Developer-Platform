# Seed backend — the owner's pre-existing personal state account (external dependency).
# Holds exactly one PDP state: this stack's. Never managed, tagged, or remediated by PDP
# (contracts/state-backend.md). Verified Entra-only (shared keys disabled) 2026-06-11.
terraform {
  backend "azurerm" {
    subscription_id      = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
    resource_group_name  = "RG-TF"
    storage_account_name = "cmhtfstatesa"
    container_name       = "tfstate"
    # Region-qualified key: the westus3 re-bootstrap builds a fresh foundations state
    # independent of the (to-be-decommissioned) eastus2 state at "pdp/foundations".
    key              = "pdp/foundations-westus3"
    use_azuread_auth = true
  }
}
