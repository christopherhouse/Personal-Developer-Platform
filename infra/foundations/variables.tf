variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

variable "owner_object_id" {
  description = "Entra object ID of the platform owner (RBAC on the PDP state container). Fixed value rather than data.azurerm_client_config so CI applies don't re-point the assignment at the CI identity."
  type        = string
  default     = "2ede4c0c-360b-47f8-80b0-bdba8badea7b"
}

variable "github_repository" {
  description = "The GitHub repository (<owner>/<repo>) whose Actions workflows authenticate via OIDC against the CI managed identity. Baked into the federated-credential subjects, so it must match the repo exactly (case-sensitive in the OIDC subject claim)."
  type        = string
  default     = "christopherhouse/Personal-Developer-Platform"

  validation {
    condition     = can(regex("^[^/]+/[^/]+$", var.github_repository))
    error_message = "github_repository must be in <owner>/<repo> form."
  }
}

locals {
  # Primary region. Moved eastus2 → westus3 (Postgres capacity in eastus2 was exhausted;
  # full platform re-bootstrap). region_short per docs/conventions.md.
  region = "westus3"

  # Pinned region-short table for constrained-name resources (docs/conventions.md).
  region_short = "wus3"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1).
  # pdp-deployed-by flipped from "owner" (bootstrap) to "github-actions" with the first
  # CI-driven apply (US3) — the stack is now managed by the rails, not the laptop.
  # pdp-platform marks this as platform-shared infrastructure so inventory (spec 005)
  # classifies it as platform, not orphan drift (docs/conventions.md §2).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-platform    = "true"
  }
}
