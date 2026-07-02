variable "platform_subscription_id" {
  description = "The platform subscription hosting all PDP platform-plane resources."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

# NOTE: there is intentionally NO `tenant_id` variable. The MCP issuer tenant is taken from
# data.azurerm_client_config.current.tenant_id (the deploy identity's tenant) so it can never be left
# empty — a blank tenant builds a malformed authority (login.microsoftonline.com//v2.0) that rejects
# every token (the live bring-up bug). The owner deploys into their own tenant, so the two are the same.

variable "mcp_audience" {
  description = "OAuth 2.1 audience the MCP endpoint validates `aud` against — the pdp-mcp Entra app registration's APPLICATION (CLIENT) ID, NOT its api:// URI (Entra v2.0 access tokens carry the resource appId GUID in `aud`). The owner reads this from scripts/bootstrap-mcp-app-registration.ps1 (the app reg is an owner-run bootstrap, not Tofu-managed — avoids a standing Entra-write CI credential). Update if the app registration is recreated."
  type        = string
  default     = "89643cfe-4041-4055-ba26-cd1d522d4fa4"
}

variable "owner_object_id" {
  description = "Entra object ID of the platform owner — the single allow-listed `oid` the MCP server authorizes (single-owner authz), and the Postgres Entra admin who runs the one-time pgaadauth principal bootstrap (research §6)."
  type        = string
  default     = "2ede4c0c-360b-47f8-80b0-bdba8badea7b"
}

# ---------------------------------------------------------------------------
# Consumed-by-reference inputs (data sources, data.tf) — this stack creates none of these.
# ---------------------------------------------------------------------------

variable "observability_resource_group_name" {
  description = "RG of the platform-shared observability stack (infra/platform-observability). The shared Log Analytics workspace + App Insights are looked up here. NOT modified by this stack."
  type        = string
  default     = "rg-pdp-westus3-observability"
}

variable "observability_workspace_name" {
  description = "Name of the platform-shared Log Analytics workspace (infra/platform-observability). The ACA env ships logs here and the stack's diagnostic_settings target it."
  type        = string
  default     = "log-pdp-westus3-platform"
}

variable "observability_appinsights_name" {
  description = "Name of the platform-shared Application Insights (infra/platform-observability). The api/mcp apps export env_id-correlated telemetry to its connection string."
  type        = string
  default     = "appi-pdp-westus3-platform"
}

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
# NEVER variables: they are seeded into Key Vault out-of-band and read at runtime via the app UAMI. The
# app/installation ids are NOT credentials (they appear in URLs + webhook payloads), so — like the
# owner/repo/branch below — they carry the live platform's values as defaults; the apps were shipping the
# `0` placeholder, which makes every dispatch fail App auth (a 0 AppId mints an invalid JWT). Override per
# variable for a different App/installation.
# ---------------------------------------------------------------------------

variable "github_app_id" {
  description = "The pdp-orchestrator GitHub App id (non-secret)."
  type        = number
  default     = 4088578
}

variable "github_app_installation_id" {
  description = "The pdp-orchestrator App installation id on the target repo's account (non-secret)."
  type        = number
  default     = 141178306
}

variable "github_repo_owner" {
  description = "Owner (org or user) of the repo hosting the dispatch workflows (the repo owner login)."
  type        = string
  default     = "christopherhouse"
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
