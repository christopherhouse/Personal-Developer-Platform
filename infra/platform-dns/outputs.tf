# Platform-shared DNS outputs — the fabric (infra/fabric) reads these to link the hub VNet
# to the shared zones, and spoke vending (spec 004) links spoke VNets to the same zones.
# Implemented in T005 (Phase 2) once the zones exist:
#   - dns_resource_group_name : string  — RG holding the global zones
#   - shared_dns_zone_ids      : map(string) — zone name → resource_id
#
# Stub only at Phase 1 (scaffolding): no resources exist yet, so there is nothing to export.
# `tofu validate` is green with zero output blocks.
