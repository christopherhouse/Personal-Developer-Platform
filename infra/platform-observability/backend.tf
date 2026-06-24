# Platform-shared observability stack state — held in the PDP state backend created by the foundations
# stack (spec 001). One state per deployable unit; this unit's key is platform/observability. It owns the
# SINGLE platform-shared Log Analytics workspace every stack ships diagnostics + app telemetry to (the
# "all resources -> Log Analytics" sink). Applied in phase 1 (with foundations/dns) so every later stack
# can reference it. Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true,
# ALWAYS. Subscription comes from ARM_SUBSCRIPTION_ID (GitHub OIDC) / az login, never pinned here.
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    key                  = "platform/observability"
    use_azuread_auth     = true
  }
}
