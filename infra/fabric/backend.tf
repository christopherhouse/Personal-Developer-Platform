# Regional hub fabric stack state — held in the PDP state backend created by the
# foundations stack (spec 001). One state per deployable unit; this unit's key is
# per-region: fabrics/<region>. East US 2 is the first region (key fabrics/eastus2);
# additional regions set their own key at init/dispatch (spec 009 ergonomics; data-model §1).
# Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true,
# ALWAYS. Subscription comes from ARM_SUBSCRIPTION_ID (GitHub OIDC) / az login context,
# never pinned here (contracts/state-backend.md).
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    key                  = "fabrics/westus3"
    use_azuread_auth     = true
  }
}
