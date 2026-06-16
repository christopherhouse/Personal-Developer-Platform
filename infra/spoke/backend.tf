# Spoke state — held in the shared PDP state backend (the foundations account, spec 001).
# One state per deployable unit; a spoke's unit is per-(target-subscription, spoke-name), so
# the key is NOT fixed here like the singleton stacks (fabric/foundations). It is supplied at
# init via PARTIAL CONFIG so the same stack code vends every spoke (data-model §1):
#
#   tofu init -input=false \
#     -backend-config="key=spokes/<target-sub-id>/<spoke-name>"
#
# e.g. key=spokes/0000.../app1 . The spoke-vend / spoke-destroy workflows pass this from the
# dispatch inputs (target_subscription_id + spoke_name); a duplicate (sub, name) targets the
# same state and converges — no duplicate spoke (FR-009).
#
# Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true, ALWAYS.
# The backend's OWN subscription (the platform sub that holds the state account) comes from the
# az login / OIDC context at init; the spoke's target subscription is a provider concern
# (versions.tf), independent of where state lives.
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    # key is intentionally omitted — set per spoke at init (-backend-config="key=...").
    use_azuread_auth = true
  }
}
