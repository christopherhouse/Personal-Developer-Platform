# Platform-shared DNS stack state — held in the PDP state backend created by the
# foundations stack (spec 001). One state per deployable unit; this unit's key is
# platform/dns (region-agnostic, created once — the global Private DNS zones live here).
# Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true,
# ALWAYS. Subscription comes from ARM_SUBSCRIPTION_ID (GitHub OIDC) / az login context,
# never pinned here (contracts/state-backend.md).
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-eastus2-foundations"
    storage_account_name = "stpdpeus2stateokoq"
    container_name       = "tfstate"
    key                  = "platform/dns"
    use_azuread_auth     = true
  }
}
