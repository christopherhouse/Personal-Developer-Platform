# Consumed-by-reference resources from the existing platform stacks. This stack CREATES none of these —
# it reads them so the host footprint stays isolated and the ledger RG is never in this stack's scope.
#
# Phase 1 (Setup) declares the file; the data sources are implemented in T010 (Foundational):
#   - data "azurerm_subnet" "aca"   — the ACA delegated subnet (declared by infra/control-plane, T008),
#                                      passed to the ACA managed environment's infrastructure_subnet_id.
#   - the private Postgres server / FQDN — for the apps' connection config (Entra-token auth; no secret).
#   - the platform-dns RG — referenced only for context; the privatelink.postgres zone is already linked
#                           to the control-plane VNet, so same-VNet ACA resolves Postgres (research §3).
