# Control-plane HOST stack (spec 007) — hosts the spec-006 control plane on Azure Container Apps,
# VNet-integrated into the existing control-plane VNet (10.0.0.0/24, same VNet as the private Postgres),
# under per-app user-assigned managed identities, plus ACR (Basic), Log Analytics, Application Insights,
# and Key Vault. CONSUMES the VNet / Postgres / private DNS by reference (data.tf) — it never touches the
# prevent_destroy + CanNotDelete ledger RG, so this footprint tears down independently (FR-016, SC-010).
#
# Phase 1 (Setup, T005) establishes the stack skeleton + locals only. Resources land in:
#   - US1 (T014-T021): RG + 3 UAMIs + ACR + Key Vault + Log Analytics + ACA env + api/ingress apps.
#   - US2 (T035/T036): the mcp container app + its identity grants.
#   - US4 (T048):      Application Insights (workspace-based).
# AVM modules + the two azurerm UAMI/role justifications are recorded in README.md (Article V).

data "azurerm_client_config" "current" {}

locals {
  # Primary region for the control plane (matches the foundations / control-plane stacks).
  region       = "westus3"
  region_short = "wus3"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1). pdp-platform marks
  # this as platform-shared infrastructure so inventory (spec 005) classifies it as platform, not orphan.
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-platform    = "true"
  }
}
