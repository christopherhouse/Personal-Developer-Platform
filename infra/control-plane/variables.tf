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

variable "owner_principal_name" {
  description = "UPN/principal name of the platform owner in the Entra tenant — the principal_name on the Postgres Entra administrator. Must match the username used to connect (az account user.name) for the same owner_object_id; the object id is the real auth key, this is the login label."
  type        = string
  default     = "chhouse@microsoft.com"
}

variable "platform_dns_resource_group_name" {
  description = "Resource group holding the platform-shared global Private DNS zones (owned by infra/platform-dns). The control-plane data-looks-up the canonical privatelink.postgres.database.azure.com zone here and links its VNet to it; it does not own the zone."
  type        = string
  default     = "rg-pdp-westus3-dns"
}

variable "observability_resource_group_name" {
  description = "RG of the platform-shared observability stack (infra/platform-observability), applied in phase 1. The shared Log Analytics workspace is looked up here as the diagnostics destination. NOT modified by this stack."
  type        = string
  default     = "rg-pdp-westus3-observability"
}

variable "observability_workspace_name" {
  description = "Name of the platform-shared Log Analytics workspace (infra/platform-observability) that this stack's resources send diagnostics to (all resources -> Log Analytics)."
  type        = string
  default     = "log-pdp-westus3-platform"
}

locals {
  # Primary region for the control plane (matches the foundations stack). Moved eastus2 →
  # westus3 (Postgres capacity); the control-plane DB is singular and lives in the primary region.
  region       = "westus3"
  region_short = "wus3"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1).
  # pdp-platform marks this as platform-shared infrastructure so inventory (spec 005)
  # classifies it as platform, not orphan drift (docs/conventions.md §2).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-platform    = "true"
  }
}
