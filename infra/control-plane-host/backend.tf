# Control-plane HOST stack state — held in the PDP state backend (foundations stack, spec 001).
# One state per deployable unit; this unit's key is platform/control-plane-host — SEPARATE from
# platform/control-plane so the host footprint (ACA + identities + ACR + observability + Key Vault)
# tears down independently and never touches the prevent_destroy + CanNotDelete ledger RG (FR-016).
# Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true, ALWAYS.
# Subscription comes from ARM_SUBSCRIPTION_ID (GitHub OIDC) / az login context, never pinned here.
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    key                  = "platform/control-plane-host"
    use_azuread_auth     = true
  }
}
