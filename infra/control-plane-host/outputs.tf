# Outputs land with their resources. US1 (T023): ACR login server, container-app FQDNs, the per-app UAMI
# names/object-ids/client-ids, and — critically — the one-time `pgaadauth_create_principal_with_oid` psql
# command(s) the owner runs to register the UAMIs as Postgres principals (research §6, SC-010 single
# reviewed manual step). The uami-mcp principal command is added in US2 (T036).

# --- Container registry -------------------------------------------------------

output "acr_login_server" {
  description = "ACR login server (e.g. crpdpwestus3controlplane.azurecr.io) — the image registry the controlplane-host-images.yml workflow pushes to and the apps pull from via UAMI."
  value       = module.acr.resource.login_server
}

# --- App endpoints ------------------------------------------------------------

output "ingress_fqdn_url" {
  description = "Public https URL of the YARP ingress app — the ONE public surface. GitHub webhook target is <this>/webhooks/github; the MCP endpoint (US2) is <this>/mcp."
  value       = module.container_app_ingress.fqdn_url
}

output "api_fqdn_url" {
  description = "Internal https URL of the api app (webhook sink + reconciler). Reachable only inside the ACA environment (via the ingress app)."
  value       = module.container_app_api.fqdn_url
}

output "mcp_fqdn_url" {
  description = "Internal https URL of the mcp app (the conversational MCP surface; scale-to-zero). Reachable only inside the ACA environment — the public path is <ingress>/mcp (US2)."
  value       = module.container_app_mcp.fqdn_url
}

# --- Per-app managed identities ----------------------------------------------

output "uami_ingress" {
  description = "uami-ingress: name + object (principal) id + client id. AcrPull only."
  value = {
    name         = azurerm_user_assigned_identity.ingress.name
    principal_id = azurerm_user_assigned_identity.ingress.principal_id
    client_id    = azurerm_user_assigned_identity.ingress.client_id
  }
}

output "uami_api" {
  description = "uami-api: name + object (principal) id + client id. Postgres principal + AcrPull + Reader + KV Secrets User."
  value = {
    name         = azurerm_user_assigned_identity.api.name
    principal_id = azurerm_user_assigned_identity.api.principal_id
    client_id    = azurerm_user_assigned_identity.api.client_id
  }
}

output "uami_mcp" {
  description = "uami-mcp: name + object (principal) id + client id. Postgres principal + AcrPull + Reader + KV Secrets User (GitHub App key)."
  value = {
    name         = azurerm_user_assigned_identity.mcp.name
    principal_id = azurerm_user_assigned_identity.mcp.principal_id
    client_id    = azurerm_user_assigned_identity.mcp.client_id
  }
}

# Application Insights is owned by infra/platform-observability now (the platform-shared telemetry stack);
# its outputs live there. This stack consumes it by data source for the apps' connection string (data.tf).

# --- Key Vault ----------------------------------------------------------------

output "key_vault_uri" {
  description = "Key Vault URI. Seed the GitHub App private key + webhook secret here out-of-band before applying the apps (runbook step 2): az keyvault secret set --vault-name <name> --name github-app-private-key/--name github-webhook-secret --file/--value ..."
  value       = module.key_vault.uri
}

# --- Coordinates for the transient-ACA-Job bootstrap helper (scripts/bootstrap-postgres-principals.sh) ---
#
# The helper creates a short-lived ACA Job in THIS stack's managed environment (so it runs IN-VNet and can
# reach the private ledger), authenticated as the owner via a freshly-minted oss-rdbms token passed as a job
# secret. These are the non-secret coordinates it needs; the owner UPN + token are supplied at runtime by az.
output "bootstrap_context" {
  description = "Non-secret coordinates for scripts/bootstrap-postgres-principals.sh: the RG + ACA environment (where the transient in-VNet psql Job runs) and the private ledger FQDN/database it connects to."
  value = {
    resource_group          = azurerm_resource_group.host.name
    aca_environment_name    = local.aca_env_name
    aca_environment_id      = module.managed_environment.resource_id
    ledger_fqdn             = data.azurerm_postgresql_flexible_server.ledger.fqdn
    postgres_database       = var.postgres_database_name
    uami_api_principal_name = azurerm_user_assigned_identity.api.name
    uami_mcp_principal_name = azurerm_user_assigned_identity.mcp.name
  }
}

# --- The one-time Postgres principal bootstrap (SC-010 single reviewed manual step) ---
#
# After apply, the owner (the Postgres Entra ADMIN) connects to the ledger from inside the VNet and runs
# this to register uami-api as a Postgres principal (matched to the UAMI by object id) and grant it
# least-privilege on the ipam + registry schemas. PREREQUISITE: scripts/run-migrations has applied the
# ipam + registry schemas (the GRANTs target those tables, so they must exist first). Idempotent on
# re-run (research §6). The uami-mcp equivalent is emitted by the US2 output below.
output "pgaadauth_bootstrap_uami_api" {
  description = "psql to run ONCE as the Entra admin (in-VNet, AFTER run-migrations) to register uami-api + grant it on ipam/registry and own the wolverine schema. The single reviewed manual step (SC-010)."
  value       = <<-EOT
    -- Connect as the Entra admin (owner) to the ledger, then run on database '${var.postgres_database_name}':
    --   psql "host=${data.azurerm_postgresql_flexible_server.ledger.fqdn} dbname=${var.postgres_database_name} user=<owner-upn> sslmode=require"
    -- PREREQUISITE: run-migrations has created the ipam + registry schemas/tables (owned by this admin).
    -- Register uami-api as an Entra principal IDEMPOTENTLY: create the LOGIN role if missing, then
    -- (re)apply the pgaadauth security label mapping it to the UAMI object id (the mechanism
    -- pgaadauth_create_principal_with_oid uses internally). Re-running repairs a MISSING principal or a
    -- STALE oid (e.g. a recreated UAMI) — pgaadauth_create_principal_with_oid instead errors if the role
    -- already exists, so it cannot self-heal a 28P01.
    DO $pg$
    BEGIN
      IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${azurerm_user_assigned_identity.api.name}') THEN
        CREATE ROLE "${azurerm_user_assigned_identity.api.name}" WITH LOGIN;
      END IF;
    END
    $pg$;
    SECURITY LABEL FOR "pgaadauth" ON ROLE "${azurerm_user_assigned_identity.api.name}"
      IS 'aadauth,oid=${azurerm_user_assigned_identity.api.principal_id},type=service';
    GRANT USAGE ON SCHEMA ipam, registry TO "${azurerm_user_assigned_identity.api.name}";
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ipam, registry TO "${azurerm_user_assigned_identity.api.name}";
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ipam, registry TO "${azurerm_user_assigned_identity.api.name}";
    ALTER DEFAULT PRIVILEGES IN SCHEMA ipam, registry GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "${azurerm_user_assigned_identity.api.name}";
    -- Wolverine message store: the api is the always-on tracking node and OWNS the wolverine schema, so
    -- its default startup auto-build creates the envelope tables/functions there. AUTHORIZATION scopes the
    -- api's DDL to this schema only (least-privilege: no database-level CREATE). mcp reaches these via
    -- membership (below); we deliberately do NOT use UseResourceSetupOnStartup (it purges envelope state).
    CREATE SCHEMA IF NOT EXISTS wolverine AUTHORIZATION "${azurerm_user_assigned_identity.api.name}";
  EOT
}

# The uami-mcp equivalent. The mcp host runs the same verb layer in-process as the api, so it writes the
# same three schemas (ipam + registry DML AND the wolverine durable outbox). Run ONCE as the Entra admin
# (in-VNet), AFTER the uami-api bootstrap (the wolverine membership references uami-api, which must exist).
# Idempotent on re-run (research §6). The mcp half of the single reviewed manual step (SC-010).
output "pgaadauth_bootstrap_uami_mcp" {
  description = "psql to run ONCE as the Entra admin (in-VNet, AFTER the uami-api bootstrap) to register uami-mcp + grant it on ipam/registry and (via membership) the api-owned wolverine store. The mcp half of SC-010."
  value       = <<-EOT
    -- Connect as the Entra admin (owner) to the ledger, then run on database '${var.postgres_database_name}':
    --   psql "host=${data.azurerm_postgresql_flexible_server.ledger.fqdn} dbname=${var.postgres_database_name} user=<owner-upn> sslmode=require"
    -- PREREQUISITE: the uami-api bootstrap has run (the GRANT below references uami-api).
    -- Register uami-mcp as an Entra principal IDEMPOTENTLY (see the uami-api bootstrap for the rationale):
    -- create the LOGIN role if missing, then (re)apply the pgaadauth security label mapping it to the UAMI
    -- object id. Re-running repairs a missing principal or a stale oid (the 28P01 self-heal).
    DO $pg$
    BEGIN
      IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${azurerm_user_assigned_identity.mcp.name}') THEN
        CREATE ROLE "${azurerm_user_assigned_identity.mcp.name}" WITH LOGIN;
      END IF;
    END
    $pg$;
    SECURITY LABEL FOR "pgaadauth" ON ROLE "${azurerm_user_assigned_identity.mcp.name}"
      IS 'aadauth,oid=${azurerm_user_assigned_identity.mcp.principal_id},type=service';
    GRANT USAGE ON SCHEMA ipam, registry TO "${azurerm_user_assigned_identity.mcp.name}";
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ipam, registry TO "${azurerm_user_assigned_identity.mcp.name}";
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ipam, registry TO "${azurerm_user_assigned_identity.mcp.name}";
    ALTER DEFAULT PRIVILEGES IN SCHEMA ipam, registry GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO "${azurerm_user_assigned_identity.mcp.name}";
    -- Wolverine message store: the api OWNS + auto-builds the wolverine envelope tables; the scale-to-zero
    -- mcp node (AutoBuildMessageStorageOnStartup=None) never creates them, it only reads/writes them. It
    -- reaches the api-owned tables via role membership — mcp INHERITs every privilege uami-api holds,
    -- including DML on tables api creates LATER (membership is role-level, so no table need exist now).
    -- Requires INHERIT (the Postgres default for pgaadauth principals); the admin can grant the role it made.
    GRANT "${azurerm_user_assigned_identity.api.name}" TO "${azurerm_user_assigned_identity.mcp.name}";
  EOT
}
