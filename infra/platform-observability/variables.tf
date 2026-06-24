variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

locals {
  # Primary region for the platform-shared observability resource group. The workspace is a regional
  # resource; later regions (spec 009) ship to this same platform-shared workspace (one pane of glass).
  region = "westus3"

  # The single platform-shared Log Analytics workspace. Platform-scoped name (NOT -controlplane): every
  # stack's resources send diagnostics here, and the workspace-based App Insights telemetry lands here too.
  law_name = "log-pdp-${local.region}-platform"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1). Platform scope:
  # NO pdp-fabric (this unit is region-agnostic platform infrastructure, not a fabric). pdp-platform marks
  # it as platform-shared so inventory (spec 005) classifies it as platform, not orphan drift.
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-platform    = "true"
  }
}
