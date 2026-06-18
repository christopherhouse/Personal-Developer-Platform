variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

variable "tenant_id" {
  description = "Entra tenant ID — the OAuth 2.1 authorization-server issuer the MCP endpoint validates JWTs against (login.microsoftonline.com/<tenant>/v2.0)."
  type        = string
  default     = "" # set in tfvars / CI; the owner's tenant.
}

variable "owner_object_id" {
  description = "Entra object ID of the platform owner — the single allow-listed `oid` the MCP server authorizes (single-owner authz), and the Postgres Entra admin who runs the one-time pgaadauth principal bootstrap (research §6)."
  type        = string
  default     = "2ede4c0c-360b-47f8-80b0-bdba8badea7b"
}

# ---------------------------------------------------------------------------
# Consumed-by-reference inputs (data sources, data.tf) — this stack creates none of these.
# ---------------------------------------------------------------------------

variable "control_plane_resource_group_name" {
  description = "RG of the existing infra/control-plane stack (holds the VNet + private Postgres). The ACA subnet and Postgres FQDN are looked up here. NOT modified by this stack."
  type        = string
  default     = "rg-pdp-westus3-controlplane"
}

variable "control_plane_vnet_name" {
  description = "Name of the existing control-plane VNet (10.0.0.0/24) the ACA subnet is carved from and the apps integrate into (same VNet as Postgres)."
  type        = string
  default     = "vnet-pdp-westus3-controlplane"
}

variable "aca_subnet_name" {
  description = "Name of the ACA delegated subnet (declared by infra/control-plane's VNet module, consumed here). 10.0.0.32/27, delegation Microsoft.App/environments (T008)."
  type        = string
  default     = "snet-pdp-westus3-aca"
}

variable "postgres_server_name" {
  description = "Name of the existing private Postgres flexible server (infra/control-plane output `server_name`). Looked up here for its private FQDN, which the api/mcp apps use to build their Entra-token connection (no password). NOT modified by this stack."
  type        = string
  default     = "psql-pdp-westus3-controlplane"
}

variable "platform_dns_resource_group_name" {
  description = "RG holding the platform-shared private DNS zones (owned by infra/platform-dns). The privatelink.postgres zone is already linked to the control-plane VNet; the same-VNet ACA apps resolve Postgres through it (research §3) — no new link needed here."
  type        = string
  default     = "rg-pdp-westus3-dns"
}
