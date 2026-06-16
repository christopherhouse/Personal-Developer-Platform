# Spoke derived values + the upstream fabric read. Everything hub-related flows from var.* and
# the fabric's PUBLISHED outputs (contracts §I1) — nothing about the hub is hardcoded (FR-010).
#
# The fabric state lives in the platform subscription's shared PDP state account (the same
# account this stack's own state uses — backend.tf), keyed per region at fabrics/<region>. This
# data source reads it directly over Entra auth (use_azuread_auth) with subscription_id pinned to
# the platform sub, so it resolves regardless of which target sub the default provider points at.
# A region whose fabric has not been applied has no state blob / no outputs there, so this read
# fails the plan fast (FR-011) — before any spoke resource is evaluated.

data "azurerm_client_config" "current" {}

data "terraform_remote_state" "fabric" {
  backend = "azurerm"

  config = {
    resource_group_name  = "rg-pdp-westus3-foundations"
    storage_account_name = "stpdpwus3statejqyq"
    container_name       = "tfstate"
    key                  = "fabrics/${var.region}"
    use_azuread_auth     = true
    subscription_id      = var.platform_subscription_id
  }
}

locals {
  region = var.region

  # Upstream fabric contract (spec 003 §I2) — read live, never hardcoded. Surfacing them as
  # locals gives every consumer one name and means a missing output (no applied fabric) surfaces
  # here rather than deep in a resource argument.
  hub_vnet_id             = data.terraform_remote_state.fabric.outputs.hub_vnet_id
  hub_resource_group_name = data.terraform_remote_state.fabric.outputs.hub_resource_group_name
  firewall_private_ip     = data.terraform_remote_state.fabric.outputs.firewall_private_ip
  shared_dns_zone_ids     = data.terraform_remote_state.fabric.outputs.shared_dns_zone_ids

  # The hub→spoke peering (main.tf T014) is created on the hub VNet, which the azurerm peering
  # resource addresses by NAME + RG (not by ID). The contract surfaces the hub VNet ID; its name
  # is the last ID segment — a pure derive, no extra upstream coupling.
  hub_vnet_name = reverse(split("/", local.hub_vnet_id))[0]

  # Spoke→shared-zone DNS links (main.tf T015) are created in the platform sub and need each
  # zone's RG + name, while the fabric publishes zone resource IDs. Parse both from the ID:
  #   /subscriptions/<s>/resourceGroups/<rg>/providers/Microsoft.Network/privateDnsZones/<zone>
  # split index 4 = RG; the last segment = the zone name (zone names contain dots, never slashes).
  dns_links = {
    for key, zone_id in local.shared_dns_zone_ids : key => {
      resource_group_name = split("/", zone_id)[4]
      zone_name           = reverse(split("/", zone_id))[0]
    }
  }

  # Universal tags + the spoke marker. pdp-spoke=<name> makes the spoke RG and its contents
  # discoverable as this spoke via Resource Graph (Article III; conventions §2). No pdp-env here
  # — that is a workload-scope tag (spec 008), not a property of the spoke.
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-spoke       = var.spoke_name
  }
}
