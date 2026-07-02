# Workload state — held in the shared PDP state backend (the foundations account, spec 001).
# One state per deployable unit; a workload's unit is per-(target-subscription, spoke, workload),
# so the key is supplied at init via PARTIAL CONFIG (FR-011):
#
#   tofu -chdir=archetypes/container-app-sql init -input=false \
#     -backend-config="key=workloads/<target-sub-id>/<spoke-name>/<workload-name>"
#
# The workload-deploy / workload-destroy workflows pass this from the dispatch inputs; a re-deploy
# of the same (sub, name) targets the same state and converges. Destroying the workload tears down
# ONLY this state — the spoke (its own state) is untouched (Article IV).
#
# Auth is Entra-only (shared keys disabled on the account) — use_azuread_auth = true, ALWAYS.
terraform {
  backend "azurerm" {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    # key is intentionally omitted — set per workload at init (-backend-config="key=...").
    use_azuread_auth = true
  }
}
