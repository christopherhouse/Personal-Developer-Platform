# Control-plane stack — the private, Entra-only Postgres that hosts the IPAM ledger
# (this spec's first schema) and, from spec 006, the rest of the control-plane database.
# State lives in the PDP backend under platform/control-plane (backend.tf). Article VI:
# the VNet range below is NOT invented here — it is the seeded reservation 10.0.0.0/24 in
# the platform supernet recorded by the IPAM migration (data-model.md §1/§5, research §12).

data "azurerm_client_config" "current" {}

resource "azurerm_resource_group" "control_plane" {
  name     = "rg-pdp-${local.region}-controlplane"
  location = local.region
  tags     = local.tags

  lifecycle {
    # Article IV (FR-015): this RG holds the live allocation ledger. A full-stack
    # `tofu destroy` hard-fails at plan time here. The server is provisioned by an AVM
    # module, so a lifecycle block cannot be injected onto the server resource itself
    # (same limitation the foundations stack documents) — the RG guard, plus the
    # CanNotDelete management lock below, is the equivalent Article-IV carve-out.
    # Removal = the reviewed protection-removal PR described in README.md.
    prevent_destroy = true
  }
}

# Management-plane protection: blocks deletes of everything in the RG (server included)
# from any tooling — portal, CLI, IaC. The complement to prevent_destroy, which only
# stops `tofu destroy`.
#
# MUST be created LAST. A CanNotDelete lock on the RG blocks the subnet child-resource
# operation (the serviceAssociationLink) that the Flexible Server performs when it injects
# into its delegated subnet — if the lock exists first, server creation fails with
# "blocking by customer lock". depends_on forces the lock after the server (and thus after
# the VNet/subnet it depends on), so the injection completes before the guard goes on.
resource "azurerm_management_lock" "control_plane" {
  name       = "lock-pdp-${local.region}-controlplane"
  scope      = azurerm_resource_group.control_plane.id
  lock_level = "CanNotDelete"
  notes      = "Article IV carve-out: holds the live IPAM ledger. Removal only via reviewed protection-removal PR (infra/control-plane/README.md)."

  depends_on = [module.postgres]
}

# ----------------------------------------------------------------------------
# Control-plane VNet + delegated subnet (private access for the Flexible Server)
# ----------------------------------------------------------------------------

# 10.0.0.0/24 — the seeded platform reservation (NOT a dynamic allocation). The delegated
# subnet cannot be resized once the server exists, so the /28 is fixed up front (research §1).
module "vnet" {
  source  = "Azure/avm-res-network-virtualnetwork/azurerm"
  version = "0.18.0"

  name          = "vnet-pdp-${local.region}-controlplane"
  location      = azurerm_resource_group.control_plane.location
  parent_id     = azurerm_resource_group.control_plane.id
  address_space = ["10.0.0.0/24"]

  subnets = {
    postgres = {
      name             = "snet-pdp-${local.region}-cp-postgres"
      address_prefixes = ["10.0.0.0/28"]
      delegations = [{
        name = "postgres-flexibleserver"
        service_delegation = {
          name = "Microsoft.DBforPostgreSQL/flexibleServers"
        }
      }]
      # The Flexible Server auto-adds a Microsoft.Storage service endpoint to its delegated
      # subnet on first provision (MS Learn: provides backbone connectivity to Azure Storage;
      # "Removing this endpoint can lead to unintended consequences"). Declare it so Terraform
      # matches Azure instead of trying to strip it. Locations are the region + its pair, as
      # Azure assigns them.
      service_endpoints_with_location = [{
        service   = "Microsoft.Storage"
        locations = [local.region, "eastus"]
      }]
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# Private DNS for the Flexible Server — uses the CENTRALIZED, canonically-named
# `privatelink.postgres.database.azure.com` zone owned by infra/platform-dns (dns RG), not a
# bespoke per-stack zone. All private DNS zones live in the dns RG (architecture.md); the
# control-plane only LINKS its VNet to the shared zone (ownership rule, mirrors the fabric).
# MS Learn: the VNet-integration zone must end in postgres.database.azure.com — the canonical
# privatelink name satisfies that; and keeping it OUT of the lock-scoped control-plane RG
# avoids the documented "CanNotDelete lock on a Postgres DNS zone breaks record updates/HA".
# ----------------------------------------------------------------------------

data "azurerm_private_dns_zone" "postgres" {
  name                = "privatelink.postgres.database.azure.com"
  resource_group_name = var.platform_dns_resource_group_name
}

resource "azurerm_private_dns_zone_virtual_network_link" "controlplane" {
  name                  = "vnetlink-pdp-${local.region}-controlplane"
  resource_group_name   = var.platform_dns_resource_group_name
  private_dns_zone_name = data.azurerm_private_dns_zone.postgres.name
  virtual_network_id    = module.vnet.resource_id
  registration_enabled  = false
  tags                  = local.tags
}

# ----------------------------------------------------------------------------
# Postgres Flexible Server — private, Entra-only, cheap, BTREE_GIST allow-listed.
# Empty server only: the IPAM schema + seed are applied by an in-VNet runtime in spec 006
# (research §13). Deployed here so the migration will succeed later.
# ----------------------------------------------------------------------------

module "postgres" {
  source  = "Azure/avm-res-dbforpostgresql-flexibleserver/azurerm"
  version = "0.2.2"

  name                = "psql-pdp-${local.region}-controlplane"
  location            = azurerm_resource_group.control_plane.location
  resource_group_name = azurerm_resource_group.control_plane.name

  server_version = "16"
  sku_name       = "B_Standard_B1ms"
  storage_mb     = 32768 # 32 GiB minimum (Article IX — smallest viable)

  # The AVM module defaults high_availability to { mode = "ZoneRedundant" }, but HA is NOT
  # supported on Burstable (B_*) SKUs — Azure rejects it with HANotSupportedForBurstableSku.
  # A single-AZ control-plane DB is the intended posture anyway (Article IX — smallest viable;
  # 30-day PITR covers recovery, FR-014). Explicitly disable HA. (Module doc: "When using a
  # Burstable SKU, set high_availability to null.")
  high_availability = null

  # Private access via VNet injection; no public endpoint ever (FR-002, Article IX).
  delegated_subnet_id           = module.vnet.subnets["postgres"].resource_id
  private_dns_zone_id           = data.azurerm_private_dns_zone.postgres.id
  public_network_access_enabled = false

  # CRITICAL override (AVM v0.2.2 smoke finding): the module's firewall_rules default is
  # `AllowAllFireWallRule` 0.0.0.0–255.255.255.255 — a public allow-all that violates FR-002.
  # A VNet-injected server takes no firewall rules; force the set empty. (README smoke note.)
  firewall_rules = {}

  # Entra-only, zero stored secrets (FR-003). No administrator_login/password is set.
  authentication = {
    active_directory_auth_enabled = true
    password_auth_enabled         = false
    tenant_id                     = data.azurerm_client_config.current.tenant_id
  }

  # Owner is the Entra admin now; the control-plane managed identity is added in spec 006
  # when a runtime exists (research §2, §13).
  ad_administrator = {
    owner = {
      tenant_id      = data.azurerm_client_config.current.tenant_id
      object_id      = var.owner_object_id
      principal_name = var.owner_principal_name
      principal_type = "User"
    }
  }

  # 30-day PITR, locally-redundant (FR-014, Article IX).
  backup_retention_days        = 30
  geo_redundant_backup_enabled = false

  # The load-bearing allow-list: btree_gist backs the GiST EXCLUDE non-overlap constraint
  # (FR-006). azure.extensions is dynamic — no restart for the allow-list (research §3).
  server_configuration = {
    btree_gist = {
      name   = "azure.extensions"
      config = "BTREE_GIST"
    }
  }

  # Ensure the hub→zone link exists before the server integrates with the zone (mirrors the
  # original design where the zone module created the link ahead of the server). The server
  # references the zone via private_dns_zone_id but not the link resource directly.
  depends_on = [azurerm_private_dns_zone_virtual_network_link.controlplane]

  enable_telemetry = false
  tags             = local.tags
}
