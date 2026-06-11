variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

locals {
  # Initial primary region (spec 001 assumption); later regions arrive with spec 009.
  region = "eastus2"

  # Pinned region-short table for constrained-name resources (docs/conventions.md).
  region_short = "eus2"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1).
  # pdp-deployed-by is "owner" during bootstrap; flips to "github-actions" with the
  # first CI-driven apply (US3).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "owner"
  }
}
