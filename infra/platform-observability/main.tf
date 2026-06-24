# Platform-shared observability stack — owns the SINGLE Log Analytics workspace that every PDP stack ships
# resource diagnostics + app telemetry to (the "all resources -> Log Analytics" sink). It lives in its own
# foundational stack (state key platform/observability), applied in phase 1 with foundations/dns, so every
# later stack (control-plane, fabric, control-plane-host, and spec-008 workloads) can reference it via a
# `data "azurerm_log_analytics_workspace"` lookup — the same loose-coupling pattern infra/platform-dns uses
# for the shared private DNS zones. This unit owns the WORKSPACE ONLY; each stack attaches its own
# resources' diagnostic_settings to it (so "all resources -> LA" is a uniform property, not per-stack hacks).

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "observability" {
  name     = "rg-pdp-${local.region}-observability"
  location = local.region
  tags     = local.tags
}

# The platform-shared workspace. PerGB2018, 30-day retention, 1 GB/day cap — the cap bounds cost and is a
# non-issue at this platform's low log throughput. Public ingest + query, set EXPLICITLY: Log Analytics has
# no private path in this design and the owner queries it from outside any VNet, so both MUST stay public —
# pinning them true stops a tightening policy from silently breaking ingestion or query.
module "log_analytics" {
  source  = "Azure/avm-res-operationalinsights-workspace/azurerm"
  version = "0.5.1"

  name                = local.law_name
  resource_group_name = azurerm_resource_group.observability.name
  location            = azurerm_resource_group.observability.location

  log_analytics_workspace_sku                        = "PerGB2018"
  log_analytics_workspace_retention_in_days          = 30
  log_analytics_workspace_daily_quota_gb             = 1
  log_analytics_workspace_internet_ingestion_enabled = true
  log_analytics_workspace_internet_query_enabled     = true

  enable_telemetry = false
  tags             = local.tags
}
