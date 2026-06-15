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
resource "azurerm_management_lock" "control_plane" {
  name       = "lock-pdp-${local.region}-controlplane"
  scope      = azurerm_resource_group.control_plane.id
  lock_level = "CanNotDelete"
  notes      = "Article IV carve-out: holds the live IPAM ledger. Removal only via reviewed protection-removal PR (infra/control-plane/README.md)."
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
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# Private DNS zone for the Flexible Server, linked to the control-plane VNet.
# The zone NAME is the DNS domain itself (docs/conventions.md §1 exception); it must end
# in postgres.database.azure.com and differ from the server name (research §1).
# ----------------------------------------------------------------------------

module "postgres_dns" {
  source  = "Azure/avm-res-network-privatednszone/azurerm"
  version = "0.5.0"

  domain_name = "pdp-controlplane.private.postgres.database.azure.com"
  parent_id   = azurerm_resource_group.control_plane.id

  virtual_network_links = {
    controlplane = {
      vnetlinkname         = "vnetlink-pdp-${local.region}-controlplane"
      vnetid               = module.vnet.resource_id
      registration_enabled = false
    }
  }

  enable_telemetry = false
  tags             = local.tags
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

  # Private access via VNet injection; no public endpoint ever (FR-002, Article IX).
  delegated_subnet_id           = module.vnet.subnets["postgres"].resource_id
  private_dns_zone_id           = module.postgres_dns.resource_id
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

  enable_telemetry = false
  tags             = local.tags
}
