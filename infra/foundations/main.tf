# PDP state backend — created greenfield; the foundations stack's own state lives in
# the seed backend (backend.tf). See specs/001-platform-foundations/contracts/state-backend.md.

resource "random_string" "state_suffix" {
  length  = 4
  lower   = true
  numeric = true
  upper   = false
  special = false
}

resource "azurerm_resource_group" "foundations" {
  name     = "rg-pdp-${local.region}-foundations"
  location = local.region
  tags     = local.tags
}

module "state_storage" {
  source  = "Azure/avm-res-storage-storageaccount/azurerm"
  version = "0.7.2"

  name      = "stpdp${local.region_short}state${random_string.state_suffix.result}"
  parent_id = azurerm_resource_group.foundations.id
  location  = azurerm_resource_group.foundations.location

  # account_replication_type is deprecated/ignored by the module (smoke-validated
  # 2026-06-12); account_sku_name is authoritative.
  account_sku_name = "Standard_LRS"

  # Zero stored secrets is a hard requirement (FR-012): Entra ID data plane only.
  shared_access_key_enabled = false

  # Article IX exception, explicitly required by this spec: GitHub-hosted runners have
  # no stable egress range, so the endpoint stays reachable. Compensating controls:
  # Entra-only auth, no anonymous access, TLS 1.2+ (research.md §3). The AVM default
  # is Disabled/Deny — these overrides are deliberate.
  public_network_access_enabled = true
  network_rules = {
    default_action = "Allow"
    bypass         = ["AzureServices"]
  }

  # FR-015: 30-day point-in-time recovery for all PDP unit state.
  blob_properties = {
    versioning_enabled = true
    delete_retention_policy = {
      enabled = true
      days    = 30
    }
    container_delete_retention_policy = {
      enabled = true
      days    = 30
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# Container as a top-level resource (not a module input) so it can carry
# prevent_destroy — lifecycle blocks cannot be injected into module internals.
# Provisioned via ARM (storage_account_id), so no data-plane RBAC is needed to create it.
resource "azurerm_storage_container" "tfstate" {
  name               = "tfstate"
  storage_account_id = module.state_storage.resource_id

  lifecycle {
    # Article IV carve-out (FR-004): all PDP unit state lives here. Removal requires
    # the protection-removal PR described in the README.
    prevent_destroy = true
  }
}

# Management-plane protection: blocks deletes from any tooling (portal, CLI, IaC).
# Inherited by the storage account and container within the RG.
resource "azurerm_management_lock" "foundations" {
  name       = "lock-pdp-${local.region}-foundations"
  scope      = azurerm_resource_group.foundations.id
  lock_level = "CanNotDelete"
  notes      = "Article IV carve-out: holds all PDP unit state. Removal only via reviewed protection-removal PR (infra/foundations/README.md)."
}

# Owner data-plane access to PDP state (FR-012: Entra RBAC, no keys).
resource "azurerm_role_assignment" "owner_state_blob" {
  scope                = module.state_storage.resource_id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = var.owner_object_id
}

# ----------------------------------------------------------------------------
# US3 (T020): GitHub CI identity — OIDC, zero stored secrets (FR-012, research §6)
# ----------------------------------------------------------------------------

data "azurerm_client_config" "current" {}

locals {
  # ARM scope for the SEED account's tfstate container (the foundations state).
  # Constructed by hand because the seed is an external dependency PDP never manages
  # (data-model.md §5) — we grant CI data-plane RBAC on it but model nothing else.
  seed_state_container_id = "/subscriptions/${var.platform_subscription_id}/resourceGroups/RG-TF/providers/Microsoft.Storage/storageAccounts/cmhtfstatesa/blobServices/default/containers/tfstate"
}

# User-assigned managed identity — no client-secret surface at all (research §6).
resource "azurerm_user_assigned_identity" "github_ci" {
  name                = "id-pdp-${local.region}-github-ci"
  resource_group_name = azurerm_resource_group.foundations.name
  location            = azurerm_resource_group.foundations.location
  tags                = local.tags
}

# Federated credential — pull_request context (plan-only).
resource "azurerm_federated_identity_credential" "github_pull_request" {
  name      = "github-pull-request"
  parent_id = azurerm_user_assigned_identity.github_ci.id
  audience  = ["api://AzureADTokenExchange"]
  issuer    = "https://token.actions.githubusercontent.com"
  subject   = "repo:${var.github_repository}:pull_request"
}

# Federated credential — main-branch context (apply).
resource "azurerm_federated_identity_credential" "github_main" {
  name      = "github-main"
  parent_id = azurerm_user_assigned_identity.github_ci.id
  audience  = ["api://AzureADTokenExchange"]
  issuer    = "https://token.actions.githubusercontent.com"
  subject   = "repo:${var.github_repository}:ref:refs/heads/main"
}

# Contributor on the platform subscription — CI applies all platform-plane IaC.
resource "azurerm_role_assignment" "ci_subscription_contributor" {
  scope                = "/subscriptions/${var.platform_subscription_id}"
  role_definition_name = "Contributor"
  principal_id         = azurerm_user_assigned_identity.github_ci.principal_id
  principal_type       = "ServicePrincipal"
}

# Data-plane RBAC on the PDP state container — CI reads/writes every unit's state.
# Container ARM ID composed from the account's resource_id (the resource's own
# resource_manager_id attribute is deprecated in azurerm 4.x).
resource "azurerm_role_assignment" "ci_pdp_state_blob" {
  scope                = "${module.state_storage.resource_id}/blobServices/default/containers/${azurerm_storage_container.tfstate.name}"
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.github_ci.principal_id
  principal_type       = "ServicePrincipal"
}

# Data-plane RBAC on the SEED tfstate container — CI reads/writes the foundations
# state (the only PDP blob in the seed). No management-plane rights, ever (contract).
resource "azurerm_role_assignment" "ci_seed_state_blob" {
  scope                = local.seed_state_container_id
  role_definition_name = "Storage Blob Data Contributor"
  principal_id         = azurerm_user_assigned_identity.github_ci.principal_id
  principal_type       = "ServicePrincipal"
}
