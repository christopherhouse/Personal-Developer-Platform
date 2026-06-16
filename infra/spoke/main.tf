# Spoke vending — one apply produces a connected, hub-egressing, NSG-protected, DNS-linked spoke
# in a TARGET subscription from a typed CIDR. Built primarily with the same AVM virtualnetwork
# module family as the hub. Everything hub-related is read live from the fabric remote state
# (locals.tf), nothing is hardcoded (FR-010). The spoke carries NO lock and NO prevent_destroy —
# it is the disposable, high-churn unit (research §7); teardown is confirm-gated in spoke-destroy.
#
# Cross-subscription (contracts §I3 / data-model §3): the spoke RG, VNet/subnets, NSGs, route
# table, and the spoke→hub peering land in the TARGET sub via the default provider; the hub→spoke
# peering and the spoke→shared-zone DNS links land in the PLATFORM sub via the aliased provider.

# ----------------------------------------------------------------------------
# T009 — Spoke resource group (target sub). pdp-spoke=<name> makes the RG and its contents
# discoverable as this spoke via Resource Graph (Article III; conventions §2). No management
# lock, no prevent_destroy — spokes are freely destroyable (research §7, FR-013).
# ----------------------------------------------------------------------------
resource "azurerm_resource_group" "spoke" {
  name     = "rg-pdp-${local.region}-spoke-${var.spoke_name}"
  location = local.region
  tags     = local.tags
}

# ----------------------------------------------------------------------------
# T011 — One NSG per spoke subnet, with NO custom rules (Azure default rule set only, FR-018).
# Every subnet is associated to its NSG via the AVM module below (FR-005). Archetypes (spec 008)
# add workload-specific rules later; the egress UDR (T012) — not the NSG — is what forces traffic
# through the hub.
# ----------------------------------------------------------------------------
resource "azurerm_network_security_group" "spoke" {
  for_each = var.subnets

  name                = "nsg-pdp-${local.region}-${var.spoke_name}-${each.key}"
  location            = azurerm_resource_group.spoke.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.tags
}

# ----------------------------------------------------------------------------
# T012 — Egress route table (Article VII / SC-004): a single 0.0.0.0/0 route to the hub firewall
# private IP (read live from the fabric, locals.tf), associated to every spoke subnet (via the AVM
# module below). No alternative default route, no NAT — the hub is the spoke's only egress.
# ----------------------------------------------------------------------------
resource "azurerm_route_table" "spoke" {
  name                = "rt-pdp-${local.region}-${var.spoke_name}"
  location            = azurerm_resource_group.spoke.location
  resource_group_name = azurerm_resource_group.spoke.name
  tags                = local.tags

  route {
    name                   = "default-to-hub-firewall"
    address_prefix         = "0.0.0.0/0"
    next_hop_type          = "VirtualAppliance"
    next_hop_in_ip_address = local.firewall_private_ip
  }
}

# ----------------------------------------------------------------------------
# T010 — Spoke VNet + subnets via the AVM virtualnetwork module. address_space is the typed
# spoke_cidr; each subnet's prefix is carved with cidrsubnet(spoke_cidr, newbits, netnum) from
# var.subnets (FR-017). Every subnet is associated to its per-subnet NSG (T011) and the shared
# egress route table (T012), and passes any service delegations straight through. The default
# var.subnets is a single workload subnet filling the whole block (newbits=0, netnum=0).
# ----------------------------------------------------------------------------
module "vnet" {
  source  = "Azure/avm-res-network-virtualnetwork/azurerm"
  version = "0.18.0"

  name          = "vnet-pdp-${local.region}-${var.spoke_name}"
  location      = azurerm_resource_group.spoke.location
  parent_id     = azurerm_resource_group.spoke.id
  address_space = [var.spoke_cidr]

  subnets = {
    for key, cfg in var.subnets : key => {
      name             = "snet-pdp-${local.region}-${var.spoke_name}-${key}"
      address_prefixes = [cidrsubnet(var.spoke_cidr, cfg.newbits, cfg.netnum)]

      # FR-005: every subnet associated to an NSG (T011) and the egress route table (T012).
      network_security_group = { id = azurerm_network_security_group.spoke[key].id }
      route_table            = { id = azurerm_route_table.spoke.id }

      # FR-017: per-subnet service delegations pass through. Input is a list of service names
      # (e.g. "Microsoft.DBforPostgreSQL/flexibleServers"); the module wants a logical delegation
      # name + the service name, so we derive a name from the service.
      delegations = [
        for d in cfg.delegations : {
          name               = replace(d, "/", "-")
          service_delegation = { name = d }
        }
      ]
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# ----------------------------------------------------------------------------
# T013 — Spoke → hub peering (target sub, default provider). Points at the hub VNet read from the
# fabric remote state. allow_forwarded_traffic=true lets the hub firewall forward spoke egress
# back out (Article VII). This is one of the TWO sides created atomically in a single vend.
# ----------------------------------------------------------------------------
resource "azurerm_virtual_network_peering" "spoke_to_hub" {
  name                      = "peer-${var.spoke_name}-to-hub"
  resource_group_name       = azurerm_resource_group.spoke.name
  virtual_network_name      = module.vnet.name
  remote_virtual_network_id = local.hub_vnet_id

  allow_forwarded_traffic      = true
  allow_virtual_network_access = true
}

# ----------------------------------------------------------------------------
# T014 — Hub → spoke peering (PLATFORM sub, aliased provider) on the hub VNet in the hub RG. The
# matching reverse side of T013, created in the same vend so the spoke comes up fully connected
# (research §2). allow_forwarded_traffic=true so the firewall can forward spoke traffic. On
# teardown this side is removed via the same aliased provider — no dangling hub peering (Article
# IV, spoke-destroy/T022).
# ----------------------------------------------------------------------------
resource "azurerm_virtual_network_peering" "hub_to_spoke" {
  provider = azurerm.platform

  name                      = "peer-hub-to-${var.spoke_name}"
  resource_group_name       = local.hub_resource_group_name
  virtual_network_name      = local.hub_vnet_name
  remote_virtual_network_id = module.vnet.resource_id

  allow_forwarded_traffic      = true
  allow_virtual_network_access = true
}

# ----------------------------------------------------------------------------
# T015 — Spoke → shared Private DNS zone links (PLATFORM sub, aliased provider), one per zone the
# fabric publishes in shared_dns_zone_ids (contracts §I1). The zones are owned by infra/platform-dns
# and live in the platform sub; the spoke only LINKS its VNet to them, so spoke teardown removes
# only these links — the shared zones survive (FR-014). registration_enabled=false: the spoke does
# not auto-register records into the shared zones (workloads create explicit private-endpoint
# records via spec 008). RG + zone name are parsed from each zone ID (locals.tf / local.dns_links).
# ----------------------------------------------------------------------------
resource "azurerm_private_dns_zone_virtual_network_link" "spoke" {
  provider = azurerm.platform

  for_each = local.dns_links

  name                  = "vnetlink-pdp-${local.region}-${var.spoke_name}-${each.key}"
  resource_group_name   = each.value.resource_group_name
  private_dns_zone_name = each.value.zone_name
  virtual_network_id    = module.vnet.resource_id
  registration_enabled  = false
  tags                  = local.tags
}
