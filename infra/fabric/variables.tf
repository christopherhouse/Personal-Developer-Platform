variable "region" {
  description = "Full Azure region name for this fabric (e.g. eastus2). Drives resource names, the location of every resource, and the pdp-fabric tag. The only other knob is region_index."
  type        = string
  default     = "westus3"
}

variable "region_index" {
  description = "The registered region's index — the 2nd octet of its /16 and the ONLY address knob. The hub carve-out is 10.<region_index>.252.0/22 (the ledger's standing reservation; spec 002). Sourced from register_region; passed by the control plane (spec 006) or set directly until then."
  type        = number
  default     = 2

  validation {
    # Index 0 is the platform supernet (10.0.0.0/16, spec 002 data-model §1); valid region
    # indices are 1–255 (one /16 per region). region_index is the only address input, so an
    # out-of-range value is caught here before any address is derived.
    condition     = floor(var.region_index) == var.region_index && var.region_index >= 1 && var.region_index <= 255
    error_message = "region_index must be an integer in [1, 255]. Index 0 is reserved for the platform supernet (10.0.0.0/16); each region owns one /16 keyed by this index."
  }
}

variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

variable "platform_dns_resource_group_name" {
  description = "Resource group holding the platform-shared global Private DNS zones (owned by infra/platform-dns). The fabric data-looks-up the zones here to link the hub VNet; it does not own them."
  type        = string
  default     = "rg-pdp-westus3-dns"
}

variable "observability_resource_group_name" {
  description = "RG of the platform-shared observability stack (infra/platform-observability). The shared Log Analytics workspace is looked up here as the diagnostics destination (Article XI). NOT modified by this stack."
  type        = string
  default     = "rg-pdp-westus3-observability"
}

variable "observability_workspace_name" {
  description = "Name of the platform-shared Log Analytics workspace (infra/platform-observability). The hub firewall/bastion/VNet/public-IP diagnostic_settings ship logs + metrics here (Article XI — observable by design)."
  type        = string
  default     = "log-pdp-westus3-platform"
}
