# Spoke stack inputs. The same code vends every spoke; behaviour is entirely input-driven
# (FR-010) — region/region_index pick the fabric and the /16, the two subscription IDs set the
# providers, spoke_name + spoke_cidr + subnets shape the spoke. No region/sub/CIDR is hardcoded
# anywhere in the stack (audited in T024).

variable "region" {
  description = "Full Azure region name for the spoke (e.g. westus3). Must have a deployed fabric (terraform_remote_state fabrics/<region>). Drives names, location, and the spoke's pdp tags."
  type        = string
  default     = "westus3"
}

variable "region_index" {
  description = "The registered region's index — the 2nd octet of its /16. The region pool is 10.<region_index>.0.0/16 and the hub carve-out is 10.<region_index>.252.0/22 (spec 002). Bounds the spoke_cidr validation. Must match the region's registration; supplied by the control plane (spec 006) or set directly until then."
  type        = number
  default     = 2

  validation {
    # Index 0 is the platform supernet (10.0.0.0/16); valid region indices are 1–255 (one /16
    # per region). Mirrors the fabric stack's identical guard.
    condition     = floor(var.region_index) == var.region_index && var.region_index >= 1 && var.region_index <= 255
    error_message = "region_index must be an integer in [1, 255]. Index 0 is reserved for the platform supernet (10.0.0.0/16); each region owns one /16 keyed by this index."
  }
}

variable "target_subscription_id" {
  description = "The subscription the spoke deploys into (default azurerm provider). MUST be writable by the CI identity. May differ from the platform subscription — this is the platform's first cross-subscription deploy (contracts §I3)."
  type        = string
}

variable "platform_subscription_id" {
  description = "The platform subscription hosting the hub VNet, the shared Private DNS zones, and the fabric state (the aliased \"platform\" provider). Used for the hub-side peering, the spoke→shared-zone DNS links, and the fabric remote-state read."
  type        = string
  default     = "8bd05b2f-62c5-4def-9869-f0617ebb3970"
}

variable "spoke_name" {
  description = "Spoke name, unique within the target subscription. Part of the identity (the spokes/<sub-id>/<spoke-name> state key) and of every spoke resource name. [a-z0-9-], 1–24 chars (the pdp-spoke tag domain)."
  type        = string

  validation {
    condition     = can(regex("^[a-z0-9-]{1,24}$", var.spoke_name))
    error_message = "spoke_name must be 1–24 chars of [a-z0-9-] (the pdp-spoke tag domain, conventions §2)."
  }
}

variable "spoke_cidr" {
  description = "The spoke's address block — a TYPED allocation fitted to the region /16 (Gate G1 / research §1; live by-size allocation is spec 006). MUST be inside 10.<region_index>.0.0/16 and outside the hub carve-out 10.<region_index>.252.0/22. Example: 10.2.16.0/24 for westus3."
  type        = string

  validation {
    # Containment in the region /16: mask the spoke's network address back to a /16 and compare
    # to the region pool. can(...) also rejects a malformed CIDR. Prefix /17–/29 keeps it a real
    # sub-block of the /16 (never the whole /16, never absurdly small).
    condition     = can(cidrhost(var.spoke_cidr, 0)) && cidrhost("${cidrhost(var.spoke_cidr, 0)}/16", 0) == "10.${var.region_index}.0.0" && tonumber(split("/", var.spoke_cidr)[1]) >= 17 && tonumber(split("/", var.spoke_cidr)[1]) <= 29
    error_message = "spoke_cidr must be a valid CIDR inside the region pool 10.<region_index>.0.0/16, with a prefix between /17 and /29."
  }

  validation {
    # Hub carve-out guard: the top /22 (third octet 252–255) is the region's standing hub
    # reservation (spec 002). Conservative check on the spoke's network octet; precise
    # cross-block non-overlap becomes the ledger's job when spec 006 records allocations.
    condition     = can(cidrhost(var.spoke_cidr, 0)) && tonumber(split(".", cidrhost(var.spoke_cidr, 0))[2]) < 252
    error_message = "spoke_cidr must not fall in the hub carve-out 10.<region_index>.252.0/22 (third octet 252–255 is reserved for the regional hub, spec 002)."
  }
}

variable "subnets" {
  description = "Configurable spoke shape (FR-017): subnet logical-name → { newbits, netnum, delegations }. Each subnet prefix is cidrsubnet(spoke_cidr, newbits, netnum); delegations passes service-delegation names straight through to the AVM module. Default: one workload subnet filling the whole block (newbits=0)."
  type = map(object({
    newbits     = number
    netnum      = number
    delegations = optional(list(string), [])
  }))
  default = {
    workload = {
      newbits     = 0
      netnum      = 0
      delegations = []
    }
  }

  validation {
    condition     = length(var.subnets) >= 1
    error_message = "At least one subnet is required (the default single workload subnet, FR-017)."
  }
}
