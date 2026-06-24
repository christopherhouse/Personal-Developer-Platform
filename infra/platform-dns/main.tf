# Platform-shared DNS stack — the platform-wide, region-agnostic set of global Azure
# Private DNS zones (privatelink.*) for PDP private endpoints. Azure Private DNS zones are
# GLOBAL resources: owning them once here (state key platform/dns) avoids the per-region
# duplicate/split-horizon zones that owning them in the fabric stack would cause (research §3,
# clarify Q3). This unit owns the ZONES ONLY — the hub→zone VNet links live in the fabric
# stack (infra/fabric), and spoke→zone links in spoke vending (spec 004). See README.md.

data "azurerm_client_config" "current" {}

# Holds the global zones. Universal tags only — platform scope, NO pdp-fabric (this unit is
# region-agnostic and is not a fabric; data-model.md §4). Location is the primary region; the
# zones themselves are global and ignore it.
resource "azurerm_resource_group" "dns" {
  name     = "rg-pdp-${local.region}-dns"
  location = local.region
  tags     = local.tags
}

# ----------------------------------------------------------------------------
# Platform-shared private-endpoint zones. Zone NAME is the DNS domain itself
# (docs/conventions.md §1.3 private-DNS exception). NO virtual_network_links here —
# links belong to fabrics/spokes (ownership rule, research §3). Grow this set by PR as
# services arrive; ACA's region-qualified zone is deferred to spec 008.
#
# Article XI (observable by design) — NOT APPLICABLE here, for three independent reasons: (1) the
# avm-res-network-privatednszone module (v0.5.0) exposes no diagnostic_settings input; (2) private DNS
# zones support only AllMetrics (no resource logs) — low-value telemetry; and (3) this stack applies in
# iac-apply Phase 1, in PARALLEL with infra/platform-observability, so it cannot reference that workspace
# without a raced dependency (only Phase 2/3 stacks wire diagnostics to it). Same phase-1 carve-out the
# foundations state account documents.
# ----------------------------------------------------------------------------

# Postgres Flexible Server private endpoints. Distinct from the control-plane's
# VNet-integrated `pdp-controlplane.private.postgres.database.azure.com` zone (a different
# zone form) — these do not conflict (research §3).
module "zone_postgres" {
  source  = "Azure/avm-res-network-privatednszone/azurerm"
  version = "0.5.0"

  domain_name = "privatelink.postgres.database.azure.com"
  parent_id   = azurerm_resource_group.dns.id

  enable_telemetry = false
  tags             = local.tags
}

# Storage / blob private endpoints.
module "zone_blob" {
  source  = "Azure/avm-res-network-privatednszone/azurerm"
  version = "0.5.0"

  domain_name = "privatelink.blob.core.windows.net"
  parent_id   = azurerm_resource_group.dns.id

  enable_telemetry = false
  tags             = local.tags
}

# Key Vault private endpoints.
module "zone_kv" {
  source  = "Azure/avm-res-network-privatednszone/azurerm"
  version = "0.5.0"

  domain_name = "privatelink.vaultcore.azure.net"
  parent_id   = azurerm_resource_group.dns.id

  enable_telemetry = false
  tags             = local.tags
}
