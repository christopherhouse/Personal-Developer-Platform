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
  description = "UPN/principal name of the platform owner in the Entra tenant — the principal_name on the Postgres Entra administrator. Must match the directory UPN for the owner_object_id (not necessarily the public email)."
  type        = string
  default     = "chris.house.00@gmail.com"
}

variable "platform_dns_resource_group_name" {
  description = "Resource group holding the platform-shared global Private DNS zones (owned by infra/platform-dns). The control-plane data-looks-up the canonical privatelink.postgres.database.azure.com zone here and links its VNet to it; it does not own the zone."
  type        = string
  default     = "rg-pdp-westus3-dns"
}

locals {
  # Primary region for the control plane (matches the foundations stack). Moved eastus2 →
  # westus3 (Postgres capacity); the control-plane DB is singular and lives in the primary region.
  region       = "westus3"
  region_short = "wus3"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1).
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
  }
}
