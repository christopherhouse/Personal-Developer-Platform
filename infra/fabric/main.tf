# Regional hub fabric — one apply stands up a complete, inventory-visible, spoke-ready hub for
# a registered Azure region: the hub VNet (from the ledger carve-out), a single Basic Azure
# Firewall as the region's controlled egress, a Basic Azure Bastion for private management, and
# the hub→shared-DNS links. Outputs (outputs.tf) are the downstream contract consumed by spoke
# vending (spec 004) and the control plane (spec 006). Article VI: the address space is the
# ledger's deterministic carve-out (locals.tf), not invented here.
#
# Deliberately NOT here (spec 004): any route table / 0.0.0.0/0 UDR (consumes the firewall
# private-IP output), spoke peering, NSGs, NAT gateway. The fabric only exposes the next-hop.

# ----------------------------------------------------------------------------
# T007 — Fabric resource group. pdp-fabric=<region> makes it (and its contents)
# discoverable as this region's hub via Resource Graph (Article III).
# ----------------------------------------------------------------------------
resource "azurerm_resource_group" "fabric" {
  name     = "rg-pdp-${local.region}-fabric"
  location = local.region
  tags     = local.tags

  lifecycle {
    # T017 / Article IV/VIII (FR-015): a hub destroy severs egress + management for the whole
    # region and would orphan spoke peerings — high blast radius. This guard hard-fails any
    # `tofu destroy` at plan time. The firewall/bastion are provisioned by AVM modules, so a
    # lifecycle block cannot be injected onto those resources directly (same limitation the
    # control-plane stack documents) — the RG-level guard + the CanNotDelete lock below is the
    # equivalent Article-IV carve-out. Deliberate teardown removes both via the fabric-destroy
    # workflow; removal of this guard is the reviewed protection-removal PR (README.md).
    prevent_destroy = true
  }
}

# T018 — Management-plane protection (Article IV/VIII): CanNotDelete locks on the FIREWALL and
# BASTION specifically — deliberately NOT on the resource group. Per-resource locks still protect
# the whole hub: you cannot delete the locked firewall/bastion, and you cannot delete the hub VNet
# or the RG while the firewall occupies AzureFirewallSubnet — so this region's egress + management
# hub stays undeletable from any plane (portal/CLI/IaC), the same guarantee an RG-scope lock gave.
#
# WHY NOT the RG (changed 2026-06-17): a CanNotDelete lock permits create/update but blocks DELETE
# of EVERY resource in its scope — including child resources of the hub VNet. The hub-side spoke
# peering (spec 004, peer-hub-to-<spoke>) is such a child, so an RG-scope lock let a spoke VEND its
# hub peering but blocked spoke TEARDOWN from deleting it → a dangling, Disconnected peering
# (Article IV violation). Scoping the lock to the firewall/bastion leaves the VNet and its peerings
# deletable, so spoke-destroy cleanly removes its hub→spoke side, while the irreplaceable egress +
# management resources stay locked.
#
# CanNotDelete permits create/update, so referencing module.*.resource[_].id (which forces the lock
# after each resource exists) does not interfere with their subnet attachments. The fabric-destroy
# teardown removes these locks via the reviewed protection-removal PR (README.md) — now two lock
# resources instead of one.
resource "azurerm_management_lock" "firewall" {
  name       = "lock-pdp-${local.region}-afw"
  scope      = module.firewall.resource.id
  lock_level = "CanNotDelete"
  notes      = "Article IV/VIII carve-out: this region's controlled egress. Removal only via the fabric-destroy workflow or a reviewed protection-removal PR (infra/fabric/README.md)."
}

resource "azurerm_management_lock" "bastion" {
  name       = "lock-pdp-${local.region}-bas"
  scope      = module.bastion.resource_id
  lock_level = "CanNotDelete"
  notes      = "Article IV/VIII carve-out: this region's private management access. Removal only via the fabric-destroy workflow or a reviewed protection-removal PR (infra/fabric/README.md)."
}

# ----------------------------------------------------------------------------
# T008 — Hub VNet from the ledger carve-out, with the three reserved Azure subnets carved by
# cidrsubnet (locals.tf). The subnet names are Azure-mandated and pass through unchanged. NO
# network_security_group and NO route_table on any of them (Azure rejects them on these
# reserved subnets — research §5).
# ----------------------------------------------------------------------------
module "vnet" {
  source  = "Azure/avm-res-network-virtualnetwork/azurerm"
  version = "0.18.0"

  name          = "vnet-pdp-${local.region}-hub"
  location      = azurerm_resource_group.fabric.location
  parent_id     = azurerm_resource_group.fabric.id
  address_space = [local.hub_address_space]

  subnets = {
    firewall = {
      name             = "AzureFirewallSubnet"
      address_prefixes = [local.firewall_subnet_prefix]
    }
    firewall_mgmt = {
      name             = "AzureFirewallManagementSubnet"
      address_prefixes = [local.firewall_mgmt_subnet_prefix]
    }
    bastion = {
      name             = "AzureBastionSubnet"
      address_prefixes = [local.bastion_subnet_prefix]
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# T009 — Three Standard/Static public IPs. Azure Firewall (any tier) requires STANDARD PIPs —
# Basic PIPs are rejected (research §1/§6). Two are for the firewall (data + the mandatory
# Basic-SKU management NIC); one for the bastion.
# ----------------------------------------------------------------------------
module "pip_fw_data" {
  source  = "Azure/avm-res-network-publicipaddress/azurerm"
  version = "0.2.1"

  name                = "pip-pdp-${local.region}-afw"
  location            = azurerm_resource_group.fabric.location
  resource_group_name = azurerm_resource_group.fabric.name

  allocation_method = "Static"
  sku               = "Standard"

  # Azure auto-applies this ip_tag to Standard PIPs attached to Firewall/Bastion. Declare it so
  # Terraform matches reality instead of stripping it — ip_tags is immutable, so a mismatch
  # forces PIP replacement, and the RG CanNotDelete lock then blocks the destroy. (Observed via
  # plan against the live PIPs.)
  ip_tags = { "FirstPartyUsage" = "/Unprivileged" }

  enable_telemetry = false
  tags             = local.tags
}

module "pip_fw_mgmt" {
  source  = "Azure/avm-res-network-publicipaddress/azurerm"
  version = "0.2.1"

  name                = "pip-pdp-${local.region}-afw-mgmt"
  location            = azurerm_resource_group.fabric.location
  resource_group_name = azurerm_resource_group.fabric.name

  allocation_method = "Static"
  sku               = "Standard"

  # Azure auto-applies this ip_tag to Standard PIPs attached to Firewall/Bastion. Declare it so
  # Terraform matches reality instead of stripping it — ip_tags is immutable, so a mismatch
  # forces PIP replacement, and the RG CanNotDelete lock then blocks the destroy. (Observed via
  # plan against the live PIPs.)
  ip_tags = { "FirstPartyUsage" = "/Unprivileged" }

  enable_telemetry = false
  tags             = local.tags
}

module "pip_bastion" {
  source  = "Azure/avm-res-network-publicipaddress/azurerm"
  version = "0.2.1"

  name                = "pip-pdp-${local.region}-bas"
  location            = azurerm_resource_group.fabric.location
  resource_group_name = azurerm_resource_group.fabric.name

  allocation_method = "Static"
  sku               = "Standard"

  # Azure auto-applies this ip_tag to Standard PIPs attached to Firewall/Bastion. Declare it so
  # Terraform matches reality instead of stripping it — ip_tags is immutable, so a mismatch
  # forces PIP replacement, and the RG CanNotDelete lock then blocks the destroy. (Observed via
  # plan against the live PIPs.)
  ip_tags = { "FirstPartyUsage" = "/Unprivileged" }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# T010 — Basic firewall policy. The SKU MUST be set to Basic to pair with the Basic firewall
# (the module default is null; a tier mismatch fails — research §6).
# ----------------------------------------------------------------------------
module "fw_policy" {
  source  = "Azure/avm-res-network-firewallpolicy/azurerm"
  version = "0.3.4"

  name                = "afwp-pdp-${local.region}-hub"
  location            = azurerm_resource_group.fabric.location
  resource_group_name = azurerm_resource_group.fabric.name

  firewall_policy_sku = "Basic"

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# T011 — Azure Firewall (Basic), the region's single controlled egress (Article VII). Basic
# SKU MANDATES a management NIC, so it carries both a data IP config (AzureFirewallSubnet +
# data PIP) and a management IP config (AzureFirewallManagementSubnet + mgmt PIP). The
# firewall's private IP is the single default-route next-hop for spokes (exposed as an output).
# ----------------------------------------------------------------------------
module "firewall" {
  source  = "Azure/avm-res-network-azurefirewall/azurerm"
  version = "0.4.0"

  name                = "afw-pdp-${local.region}-hub"
  location            = azurerm_resource_group.fabric.location
  resource_group_name = azurerm_resource_group.fabric.name

  firewall_sku_tier  = "Basic"
  firewall_sku_name  = "AZFW_VNet"
  firewall_policy_id = module.fw_policy.resource_id

  # ip_configurations is the current (map) input; the list-form firewall_ip_configuration is
  # deprecated and the module rejects mixing the two (research §6). Exactly one config carries
  # the data subnet_id (module validation enforces this); the management NIC is configured
  # separately below (mandatory for Basic SKU).
  ip_configurations = {
    data = {
      name                 = "afw-ipconfig-data"
      subnet_id            = module.vnet.subnets["firewall"].resource_id
      public_ip_address_id = module.pip_fw_data.public_ip_id
    }
  }

  firewall_management_ip_configuration = {
    name                 = "afw-ipconfig-mgmt"
    subnet_id            = module.vnet.subnets["firewall_mgmt"].resource_id
    public_ip_address_id = module.pip_fw_mgmt.public_ip_id
  }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# T012 — Azure Bastion (Basic) for private management, no public endpoint reachable for the
# managed plane (Article IX). Basic is the floor SKU that supports VNet peering, so this single
# hub bastion reaches VMs in peered spokes (Developer SKU rejected — no peering, research §2).
# ----------------------------------------------------------------------------
module "bastion" {
  source  = "Azure/avm-res-network-bastionhost/azurerm"
  version = "0.9.0"

  name      = "bas-pdp-${local.region}-hub"
  location  = azurerm_resource_group.fabric.location
  parent_id = azurerm_resource_group.fabric.id

  sku = "Basic"

  ip_configuration = {
    name                 = "bas-ipconfig"
    subnet_id            = module.vnet.subnets["bastion"].resource_id
    create_public_ip     = false
    public_ip_address_id = module.pip_bastion.public_ip_id
  }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# T013 — Hub → platform-shared Private DNS zone links. The zones are OWNED by infra/platform-dns
# (global resources, region-agnostic); the fabric only LINKS the hub to them (research §3,
# clarify Q3). Zones are looked up by name via data sources (loose coupling — no remote state),
# so fabric teardown removes only these links; the shared zones survive (FR-006/FR-014).
# registration_enabled=false: the hub does not auto-register records into these zones.
# ----------------------------------------------------------------------------
locals {
  # Shared zone domain → short key for link naming and the shared_dns_zone_ids output map.
  shared_dns_zones = {
    postgres = "privatelink.postgres.database.azure.com"
    blob     = "privatelink.blob.core.windows.net"
    kv       = "privatelink.vaultcore.azure.net"
  }
}

data "azurerm_private_dns_zone" "shared" {
  for_each = local.shared_dns_zones

  name                = each.value
  resource_group_name = var.platform_dns_resource_group_name
}

resource "azurerm_private_dns_zone_virtual_network_link" "hub" {
  for_each = local.shared_dns_zones

  name                  = "vnetlink-pdp-${local.region}-hub-${each.key}"
  resource_group_name   = var.platform_dns_resource_group_name
  private_dns_zone_name = data.azurerm_private_dns_zone.shared[each.key].name
  virtual_network_id    = module.vnet.resource_id
  registration_enabled  = false
  tags                  = local.tags
}
