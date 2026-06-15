variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

variable "owner_object_id" {
  description = "Entra object ID of the platform owner — set as the Entra administrator on the control-plane Postgres (password auth is disabled; Entra-only). Fixed value rather than data.azurerm_client_config so CI applies don't re-point the admin at the CI identity."
  type        = string
  default     = "2ede4c0c-360b-47f8-80b0-bdba8badea7b"
}

locals {
  # Primary region for the control plane (matches the foundations stack). Later regions
  # arrive with spec 009; the control-plane DB is singular and lives in the primary region.
  region       = "eastus2"
  region_short = "eus2"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
  }
}
