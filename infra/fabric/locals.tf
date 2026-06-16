# Derived values for the regional hub fabric. The ONLY address knob is var.region_index
# (validated 1–255 in variables.tf); everything addressable flows from it (FR-016 — a second
# region needs only region + region_index changed, no code edits).

data "azurerm_client_config" "current" {}

locals {
  # Region name drives every resource name, the location of every resource, and the
  # pdp-fabric tag. (region_short from conventions §1.2 is only needed for length-constrained
  # names — none exist in this stack, so it is intentionally omitted; data-model §2.)
  region = var.region

  # The ledger's standing hub carve-out: the top /22 of the region's /16 (spec 002 data-model
  # §1/§2). The fabric MIRRORS this deterministically from the registered region_index — it
  # does not invent address space and creates no allocation row (Article VI; FR-003). For
  # eastus2 (index 1): 10.1.252.0/22.
  hub_address_space = "10.${var.region_index}.252.0/22"

  # Reserved Azure subnet prefixes carved from the /22 with cidrsubnet(hub, 4, n) — 16 × /26,
  # three used, thirteen of headroom (research §5). The names are Azure-mandated and must be
  # exact; per Azure rules NO NSG and NO route table is attached to any of them (the stack
  # simply omits both — the AVM module will not stop you).
  firewall_subnet_prefix      = cidrsubnet(local.hub_address_space, 4, 0) # AzureFirewallSubnet            10.R.252.0/26
  firewall_mgmt_subnet_prefix = cidrsubnet(local.hub_address_space, 4, 1) # AzureFirewallManagementSubnet  10.R.252.64/26
  bastion_subnet_prefix       = cidrsubnet(local.hub_address_space, 4, 2) # AzureBastionSubnet             10.R.252.128/26

  # Universal tags + the fabric marker. pdp-fabric=<region> makes the RG and its contents
  # discoverable as this region's hub via Resource Graph (Article III; SC-003).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-fabric      = var.region
  }
}
