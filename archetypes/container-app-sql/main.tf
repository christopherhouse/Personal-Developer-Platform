# container-app-sql — the first workload archetype (spec 008): a containerized app on the spoke's
# SHARED ACA environment plus an Azure SQL SERVERLESS (auto-pause) database, private by default,
# credential-free end to end. One isolated state per workload; destroying it removes the RG + app +
# SQL + private endpoint and leaves the spoke untouched (Article IV).
#
# Identity model (R6): ONE user-assigned managed identity is the app's identity AND the SQL server's
# Entra administrator — zero passwords, zero stored secrets anywhere. The app connects with
# `Authentication=Active Directory Managed Identity` in the connection string.
# Network model: the app's ingress is INTERNAL unless parameters.publicEndpoint opts in (visible in
# the surfaced plan — Article IX); SQL has public access disabled and is reachable only through a
# private endpoint in the spoke's `workload` subnet, resolving via the shared
# privatelink.database.windows.net zone the spoke is already linked to.

locals {
  region = var.region

  # Upstream spoke contract (infra/spoke outputs) — read live, never hardcoded (FR-012).
  spoke_aca_environment_id = data.terraform_remote_state.spoke.outputs.spoke_aca_environment_id
  spoke_workload_subnet_id = data.terraform_remote_state.spoke.outputs.spoke_subnets["workload"]

  # Conditional platform-ACR pull: image refs targeting the platform registry get automatic
  # managed-identity pull (AcrPull below); anything else must be publicly resolvable — no
  # credentials are ever accepted as parameters (contracts/archetype-catalog.md).
  platform_acr_login_server = "${var.platform_acr_name}.azurecr.io"
  uses_platform_acr         = startswith(var.parameters.containerImage, "${var.platform_acr_name}.azurecr.io/")

  # Resource names (CAF-style; `wl` keeps the UAMI under Entra's display-name comfort zone).
  resource_group_name = "rg-pdp-${local.region}-workload-${var.workload_name}"
  uami_name           = "uami-pdp-${local.region}-wl-${var.workload_name}"
  container_app_name  = "ca-pdp-${local.region}-${var.workload_name}"
  sql_server_name     = "sql-pdp-${local.region}-${var.workload_name}"
  sql_database_name   = "sqldb-${var.workload_name}"

  # The full workload tag set (FR-013): universal tags + the workload scope + the environment
  # grouping key. pdp-env is what ListWorkloadEnvironments groups by (SC-004) — no inventory
  # code change needed.
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-workload    = var.workload_name
    pdp-env         = var.pdp_env
  }

  # Password-less SQL connection for the app (R6): Entra token via the UAMI —
  # `Authentication=Active Directory Managed Identity` + the UAMI client id as User Id. The private
  # FQDN resolves through the spoke-linked privatelink zone to the private endpoint.
  sql_server_fqdn = "${local.sql_server_name}.database.windows.net"
  sql_connection_string = join(";", [
    "Server=tcp:${local.sql_server_fqdn},1433",
    "Initial Catalog=${local.sql_database_name}",
    "Authentication=Active Directory Managed Identity",
    "User Id=${azurerm_user_assigned_identity.workload.client_id}",
    "Encrypt=True",
  ])
}

# ---------------------------------------------------------------------------
# Workload resource group (target sub) — the destroy unit. pdp-workload + pdp-env make the RG and
# its contents discoverable via Resource Graph (Article III). No lock, no prevent_destroy: workloads
# are the highest-churn, freely-destroyable unit (Article IV).
# ---------------------------------------------------------------------------
resource "azurerm_resource_group" "workload" {
  name     = local.resource_group_name
  location = local.region
  tags     = local.tags
}

# ---------------------------------------------------------------------------
# The workload's identity. Article V: no AVM composition exists for a user-assigned managed
# identity, so azurerm_user_assigned_identity is used directly (justified in README.md — the
# spec-007 precedent). It is simultaneously: the container app's identity, the (conditional) ACR
# pull identity, and the SQL server's Entra administrator (R6 — recorded trade-off in README.md).
# ---------------------------------------------------------------------------
resource "azurerm_user_assigned_identity" "workload" {
  name                = local.uami_name
  resource_group_name = azurerm_resource_group.workload.name
  location            = azurerm_resource_group.workload.location
  tags                = local.tags
}

# Conditional AcrPull on the PLATFORM registry (platform sub, aliased provider): granted only when
# the image reference targets it. Article V: role assignments are first-class provider resources
# (no AVM module) — justified in README.md.
resource "azurerm_role_assignment" "acr_pull" {
  provider = azurerm.platform

  count = local.uses_platform_acr ? 1 : 0

  scope                = data.azurerm_container_registry.platform[0].id
  role_definition_name = "AcrPull"
  principal_id         = azurerm_user_assigned_identity.workload.principal_id
  principal_type       = "ServicePrincipal"
}

# ---------------------------------------------------------------------------
# The container app — on the SPOKE'S shared ACA environment (cross-RG by ID; R5). INTERNAL ingress
# unless parameters.publicEndpoint opts in (Article IX; the opt-in is visible in the surfaced plan).
# Consumption workload profile: scale-to-zero, $0 idle, smallest viable resources (SC-005).
# ---------------------------------------------------------------------------
module "container_app" {
  source  = "Azure/avm-res-app-containerapp/azurerm"
  version = "0.9.0"

  name                                  = local.container_app_name
  resource_group_name                   = azurerm_resource_group.workload.name
  resource_group_id                     = azurerm_resource_group.workload.id
  location                              = azurerm_resource_group.workload.location
  container_app_environment_resource_id = local.spoke_aca_environment_id

  revision_mode         = "Single"
  workload_profile_name = "Consumption"

  managed_identities = {
    user_assigned_resource_ids = [azurerm_user_assigned_identity.workload.id]
  }

  # Credential-free pull for platform-ACR image refs (AcrPull above); public refs need no registry.
  registries = local.uses_platform_acr ? [{
    server   = local.platform_acr_login_server
    identity = azurerm_user_assigned_identity.workload.id
  }] : []

  ingress = {
    external_enabled = var.parameters.publicEndpoint # default false — private by default (Article IX)
    target_port      = var.parameters.targetPort
    transport        = "auto"
    traffic_weight = [{
      latest_revision = true
      percentage      = 100
    }]
  }

  template = {
    min_replicas = 0 # scale-to-zero: a workload idles at $0 compute (Article IX)
    max_replicas = 3
    containers = [{
      name   = "app"
      image  = var.parameters.containerImage
      cpu    = var.parameters.cpu
      memory = var.parameters.memory
      env = [
        # Password-less SQL (R6): the stamped app repo template reads ConnectionStrings__Sql;
        # Microsoft.Data.SqlClient acquires the Entra token through the UAMI — no secret exists.
        { name = "ConnectionStrings__Sql", value = local.sql_connection_string },
        # Lets DefaultAzureCredential-style callers in the container bind to the UAMI directly.
        { name = "AZURE_CLIENT_ID", value = azurerm_user_assigned_identity.workload.client_id },
      ]
    }]
  }

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# Azure SQL — serverless, auto-pausing, Entra-only, private-endpoint-only (R6). AVM
# avm-res-sql-server 0.2.1 (new family adoption — Article V smoke-validation recorded in README.md).
# The database is GP_S_Gen5_1 serverless with min_capacity 0.5: compute bills $0 while paused
# (auto_pause after parameters.sqlAutoPauseDelayMinutes), the smallest viable tier (SC-005).
# ---------------------------------------------------------------------------
module "sql_server" {
  source  = "Azure/avm-res-sql-server/azurerm"
  version = "0.2.1"

  name                = local.sql_server_name
  resource_group_name = azurerm_resource_group.workload.name
  location            = azurerm_resource_group.workload.location
  server_version      = "12.0"

  # Entra-ONLY auth (zero SQL passwords anywhere): the workload UAMI is the server's Entra
  # administrator (R6 — sidesteps the per-user CREATE USER bootstrap no pipeline can run;
  # acceptable for a single-owner archetype v1, recorded in README.md).
  azuread_administrator = {
    azuread_authentication_only = true
    login_username              = local.uami_name
    object_id                   = azurerm_user_assigned_identity.workload.principal_id
    tenant_id                   = data.azurerm_client_config.current.tenant_id
  }

  # Private-endpoint-only (Article IX): no public path exists regardless of firewall rules.
  public_network_access_enabled = false

  private_endpoints = {
    sql = {
      subnet_resource_id            = local.spoke_workload_subnet_id
      subresource_name              = "sqlServer"
      private_dns_zone_resource_ids = [data.azurerm_private_dns_zone.sql.id]
      tags                          = local.tags
    }
  }

  databases = {
    app = {
      name                        = local.sql_database_name
      sku_name                    = "GP_S_Gen5_1" # serverless, 1 vCore max — smallest viable (SC-005)
      min_capacity                = 0.5
      auto_pause_delay_in_minutes = var.parameters.sqlAutoPauseDelayMinutes
      max_size_gb                 = var.parameters.sqlMaxSizeGb

      # Ship database diagnostics to the platform-shared workspace (Article XI / SC-006).
      diagnostic_settings = {
        to_la = {
          name                  = "to-log-analytics"
          workspace_resource_id = data.azurerm_log_analytics_workspace.platform.id
        }
      }
    }
  }

  # Ship server-level diagnostics to the platform-shared workspace (Article XI / SC-006).
  diagnostic_settings = {
    to_la = {
      name                  = "to-log-analytics"
      workspace_resource_id = data.azurerm_log_analytics_workspace.platform.id
    }
  }

  enable_telemetry = false
  tags             = local.tags
}
