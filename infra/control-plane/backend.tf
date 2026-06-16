# Control-plane stack state — held in the PDP state backend created by the foundations
# stack (spec 001). One state per deployable unit; this unit's key is
# platform/control-plane (key registry: specs/002-ipam-ledger/data-model.md §3).
# Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true,
# ALWAYS. Subscription comes from ARM_SUBSCRIPTION_ID (GitHub OIDC) / az login context,
# never pinned here (contracts/state-backend.md).
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    key                  = "platform/control-plane"
    use_azuread_auth     = true
  }
}
