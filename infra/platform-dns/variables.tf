variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

locals {
  # Primary region for the platform-shared DNS resource group. The Private DNS zones
  # themselves are global; only the RG that holds them carries a location (matches the
  # foundations/control-plane stacks). Later regions reuse these same global zones.
  region       = "westus3"
  region_short = "wus3"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1).
  # Platform scope: NO pdp-fabric tag (this unit is region-agnostic; zones are not a fabric).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
  }
}
