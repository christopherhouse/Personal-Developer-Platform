# container-app-sql inputs. EVERY input is supplied by the workload-deploy/destroy workflows from
# the control plane's dispatch (contracts/execution-plane.md) — the caller never sets them directly.
# The caller's surface is `parameters` alone, already validated against this archetype's JSON schema
# BEFORE dispatch (FR-003); the module re-declares the same defaults so the stored parameters stay
# exactly what the caller sent (defaults applied here, never injected by the control plane).

variable "region" {
  description = "Full Azure region name of the containing spoke (e.g. westus3). Drives names, location, and tags."
  type        = string
  default     = "westus3"
}

variable "target_subscription_id" {
  description = "The subscription the spoke — and therefore the workload — lives in (default azurerm provider)."
  type        = string
}

variable "platform_subscription_id" {
  description = "The platform subscription hosting the shared Log Analytics workspace, the shared Private DNS zones, the platform ACR, and the spoke state (the aliased \"platform\" provider)."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

variable "spoke_name" {
  description = "The containing spoke — locates its remote state (spokes/<sub>/<name>) for the shared ACA environment and the private-endpoint subnet."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9-]{1,24}$", var.spoke_name))
    error_message = "spoke_name must be 1–24 chars of [a-z0-9-] (the pdp-spoke tag domain)."
  }
}

variable "workload_name" {
  description = "Workload name, unique within the target subscription. Part of the identity (the workloads/<sub>/<spoke>/<name> state key) and of every workload resource name (the pdp-workload tag domain)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9-]{1,24}$", var.workload_name))
    error_message = "workload_name must be 1–24 chars of [a-z0-9-] (the pdp-workload tag domain)."
  }
}

variable "pdp_env" {
  description = "The workload's environment grouping value (the pdp-env tag), e.g. dev. This is what makes ListWorkloadEnvironments light up with zero inventory code change (SC-004)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9-]{1,16}$", var.pdp_env))
    error_message = "pdp_env must be 1–16 chars of [a-z0-9-] (the pdp-env tag domain)."
  }
}

variable "parameters" {
  description = "The caller's archetype parameters — schema-validated by the control plane before dispatch (contracts/archetype-catalog.md), passed whole as TF_VAR_parameters. Defaults here mirror the JSON schema's documented defaults."
  type = object({
    containerImage           = string
    publicEndpoint           = optional(bool, false)
    targetPort               = optional(number, 8080)
    cpu                      = optional(number, 0.25)
    memory                   = optional(string, "0.5Gi")
    sqlAutoPauseDelayMinutes = optional(number, 60)
    sqlMaxSizeGb             = optional(number, 2)
  })
}

# ---------------------------------------------------------------------------
# Consumed-by-reference lookups (data.tf) — this module creates NONE of these.
# ---------------------------------------------------------------------------

variable "platform_dns_resource_group_name" {
  description = "RG of the platform-shared Private DNS zones (infra/platform-dns). The privatelink.database.windows.net zone is looked up here for the SQL private endpoint's zone group. NOT modified by this module."
  type        = string
  default     = "rg-pdp-westus3-dns"
}

variable "observability_resource_group_name" {
  description = "RG of the platform-shared observability stack (infra/platform-observability). The shared Log Analytics workspace is looked up here (Article XI). NOT modified by this module."
  type        = string
  default     = "rg-pdp-westus3-observability"
}

variable "observability_workspace_name" {
  description = "Name of the platform-shared Log Analytics workspace (infra/platform-observability). SQL diagnostics ship here (Article XI)."
  type        = string
  default     = "log-pdp-westus3-platform"
}

variable "platform_acr_name" {
  description = "Name of the platform container registry (infra/control-plane-host). When parameters.containerImage references it, the workload UAMI is granted AcrPull there automatically (credential-free pull); any other image reference must be publicly resolvable."
  type        = string
  default     = "crpdpwestus3controlplane"
}

variable "platform_acr_resource_group_name" {
  description = "RG holding the platform container registry (infra/control-plane-host). NOT modified by this module beyond the conditional AcrPull grant."
  type        = string
  default     = "rg-pdp-westus3-controlplane-host"
}
