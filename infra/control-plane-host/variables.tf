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

# ---------------------------------------------------------------------------
# Postgres (the existing private ledger) — connection coordinates the apps use.
# ---------------------------------------------------------------------------

variable "postgres_database_name" {
  description = "Database on the existing flexible server that holds the ipam/registry/wolverine schemas (spec 006). Matches the migrated database (local-dev convention is `pdp`); pinned at live deploy (T026) against the actual migrated DB."
  type        = string
  default     = "pdp"
}

# ---------------------------------------------------------------------------
# Container images — pushed to this stack's ACR by controlplane-host-images.yml over OIDC (T024).
# ---------------------------------------------------------------------------

variable "image_tag" {
  description = "Image tag to deploy for all three apps (e.g. the commit SHA the images workflow pushed). `latest` for first bring-up; pin to a SHA for reproducible deploys."
  type        = string
  default     = "latest"
}

variable "api_image_repository" {
  description = "ACR repository name for the Pdp.ControlPlane.Api image."
  type        = string
  default     = "pdp-api"
}

variable "ingress_image_repository" {
  description = "ACR repository name for the Pdp.ControlPlane.Ingress (YARP) image."
  type        = string
  default     = "pdp-ingress"
}

variable "mcp_image_repository" {
  description = "ACR repository name for the Pdp.Mcp image (consumed by the mcp app in US2/T035)."
  type        = string
  default     = "pdp-mcp"
}

# ---------------------------------------------------------------------------
# GitHub App (pdp-orchestrator) — NON-SECRET coordinates only. The private key + webhook HMAC secret are
# NEVER variables: they are seeded into Key Vault out-of-band and read at runtime via the app UAMI.
# ---------------------------------------------------------------------------

variable "github_app_id" {
  description = "The pdp-orchestrator GitHub App id (non-secret)."
  type        = number
  default     = 0 # set in tfvars / CI for the live deploy
}

variable "github_app_installation_id" {
  description = "The pdp-orchestrator App installation id on the target repo's account (non-secret)."
  type        = number
  default     = 0 # set in tfvars / CI for the live deploy
}

variable "github_repo_owner" {
  description = "Owner (org or user) of the repo hosting the dispatch workflows."
  type        = string
  default     = "Personal-Developer-Platform"
}

variable "github_repo_name" {
  description = "Name of the repo hosting the dispatch workflows."
  type        = string
  default     = "Personal-Developer-Platform"
}

variable "github_default_branch" {
  description = "Default branch the control plane dispatches workflow_dispatch against."
  type        = string
  default     = "main"
}
