# Control-plane HOST stack (spec 007) — hosts the spec-006 control plane on Azure Container Apps,
# VNet-integrated into the existing control-plane VNet (10.0.0.0/24, same VNet as the private Postgres),
# under per-app user-assigned managed identities, plus ACR (Basic), Log Analytics, Application Insights,
# and Key Vault. CONSUMES the VNet / Postgres / private DNS by reference (data.tf) — it never touches the
# prevent_destroy + CanNotDelete ledger RG, so this footprint tears down independently (FR-016, SC-010).
#
# Resources land per user story:
#   - US1 (T014-T021): RG + 3 UAMIs + ACR + Key Vault + Log Analytics + ACA env + api/ingress apps.
#   - US2 (T035/T036): the mcp container app + its identity grants.
#   - US4 (T048):      Application Insights (workspace-based).
# AVM modules + the two azurerm UAMI/role justifications are recorded in README.md (Article V).

data "azurerm_client_config" "current" {}

locals {
  # Primary region for the control plane (matches the foundations / control-plane stacks).
  region       = "westus3"
  region_short = "wus3"

  # Universal tags — required on every PDP-managed resource group (data-model.md §1). pdp-platform marks
  # this as platform-shared infrastructure so inventory (spec 005) classifies it as platform, not orphan.
  tags = {
    pdp-managed     = "true"
    pdp-deployed-by = "github-actions"
    pdp-platform    = "true"
  }

  # Resource names (CAF-style <type>-pdp-<region>-<name>; ACR/KV drop hyphens per their name rules).
  resource_group_name = "rg-pdp-${local.region}-controlplane-host"
  acr_name            = "crpdp${local.region}controlplane" # alnum 5-50, no hyphens
  key_vault_name      = "kv-pdp-${local.region}-cph"       # <=24 chars, starts letter, no `--`
  law_name            = "log-pdp-${local.region}-controlplane"
  appinsights_name    = "appi-pdp-${local.region}-controlplane"
  aca_env_name        = "cae-pdp-${local.region}-controlplane"
  app_name_ingress    = "ca-pdp-${local.region}-ingress"
  app_name_api        = "ca-pdp-${local.region}-api"
  app_name_mcp        = "ca-pdp-${local.region}-mcp"

  # Per-app UAMI display names. These are ALSO the Postgres principal names — the owner's one-time
  # `pgaadauth_create_principal_with_oid` registers a role of EXACTLY this name, matched to the UAMI by
  # object id, and the apps connect with Username = this name (research §5/§6).
  uami_name_ingress = "uami-pdp-${local.region}-ingress"
  uami_name_api     = "uami-pdp-${local.region}-api"
  uami_name_mcp     = "uami-pdp-${local.region}-mcp"

  # The ACA secret names the container apps expose to their containers (lowercase alnum + hyphen). They
  # map 1:1 onto the Key Vault secret names; the VALUE is seeded out-of-band (see below) and resolved at
  # runtime via the app UAMI — it never lands in tofu state (SC-007).
  secret_name_gh_app_key   = "github-app-private-key"
  secret_name_gh_webhook   = "github-webhook-secret"
  kv_secret_uri_gh_app_key = "${module.key_vault.uri}secrets/${local.secret_name_gh_app_key}"
  kv_secret_uri_gh_webhook = "${module.key_vault.uri}secrets/${local.secret_name_gh_webhook}"

  # Password-LESS Postgres connection strings (Entra-token auth; research §5). Host = the private FQDN of
  # the existing flexible server (data source); Username = the per-app UAMI display name = the pgaadauth
  # principal. NO password — EntraPostgres supplies a managed-identity token via Npgsql's periodic-password
  # provider at runtime. SSL is required by the flexible server.
  postgres_connection_string_api = join(";", [
    "Host=${data.azurerm_postgresql_flexible_server.ledger.fqdn}",
    "Database=${var.postgres_database_name}",
    "Username=${local.uami_name_api}",
    "Ssl Mode=Require",
    "Pooling=true",
  ])

  # The mcp app's password-less connection — identical shape, Username = the uami-mcp principal (it hosts
  # the verb layer in-process like the CLI, so it is a Postgres principal too — data-model §2).
  postgres_connection_string_mcp = join(";", [
    "Host=${data.azurerm_postgresql_flexible_server.ledger.fqdn}",
    "Database=${var.postgres_database_name}",
    "Username=${local.uami_name_mcp}",
    "Ssl Mode=Require",
    "Pooling=true",
  ])

  # The MCP endpoint's OAuth 2.1 protected-resource audience. NOTE: this is the pdp-mcp app registration's
  # APPLICATION (CLIENT) ID, not its api:// URI — Entra v2.0 access tokens always carry the resource's appId
  # GUID in `aud` (the App ID URI only appears in the scope's `scp` prefix). The JWT bearer validation
  # matches `aud` against this exact value. Supplied via var.mcp_audience (the owner sets it from the
  # bootstrap-mcp-app-registration.ps1 output — the app reg is an owner-run bootstrap, not Tofu-managed,
  # to avoid a standing Entra-write CI credential). (contracts/identity-and-auth.md §B.)
  mcp_audience = var.mcp_audience

  # Non-secret GitHub App config (ids + repo coordinates). The PRIVATE KEY and WEBHOOK SECRET are NOT here
  # — they arrive only as Key Vault-backed ACA secrets. These plain values are safe in config/state.
  github_app_env = {
    GitHubApp__AppId          = tostring(var.github_app_id)
    GitHubApp__InstallationId = tostring(var.github_app_installation_id)
    GitHubApp__Owner          = var.github_repo_owner
    GitHubApp__Repository     = var.github_repo_name
    GitHubApp__DefaultBranch  = var.github_default_branch
  }
}

# ---------------------------------------------------------------------------
# T014 — Resource group + per-app user-assigned managed identities.
# The RG carries the pdp-* tag schema and has NO prevent_destroy / lock — this whole stack is destroyable
# (Article IV); the protection lives on the SEPARATE ledger RG, never here.
# Article V: there is no AVM module for a user-assigned managed identity, so azurerm_user_assigned_identity
# is used directly (justified in README.md). One UAMI per app = least privilege (data-model §2).
# ---------------------------------------------------------------------------

resource "azurerm_resource_group" "host" {
  name     = local.resource_group_name
  location = local.region
  tags     = local.tags
}

resource "azurerm_user_assigned_identity" "ingress" {
  name                = local.uami_name_ingress
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "api" {
  name                = local.uami_name_api
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location
  tags                = local.tags
}

resource "azurerm_user_assigned_identity" "mcp" {
  name                = local.uami_name_mcp
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location
  tags                = local.tags
}

# ---------------------------------------------------------------------------
# T015 — Azure Container Registry (Basic) + AcrPull to all three app UAMIs.
# Basic SKU is the smallest, inherently private-pull-only registry (Article IX; research §7). Admin user
# stays disabled; image pull is credential-free via each app's UAMI (registries{ identity } on the apps).
# ---------------------------------------------------------------------------

module "acr" {
  source  = "Azure/avm-res-containerregistry-registry/azurerm"
  version = "0.5.1"

  name                = local.acr_name
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location

  sku                     = "Basic"
  admin_enabled           = false
  zone_redundancy_enabled = false # module forces this off for non-Premium anyway; explicit for clarity

  # Credential-free pull: grant AcrPull to each app's UAMI principal (data-model §2). UAMI principals are
  # service principals in Entra, hence principal_type = "ServicePrincipal".
  role_assignments = {
    ingress = {
      role_definition_id_or_name = "AcrPull"
      principal_id               = azurerm_user_assigned_identity.ingress.principal_id
      principal_type             = "ServicePrincipal"
    }
    api = {
      role_definition_id_or_name = "AcrPull"
      principal_id               = azurerm_user_assigned_identity.api.principal_id
      principal_type             = "ServicePrincipal"
    }
    mcp = {
      role_definition_id_or_name = "AcrPull"
      principal_id               = azurerm_user_assigned_identity.mcp.principal_id
      principal_type             = "ServicePrincipal"
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# T016 — Key Vault (Standard, RBAC) holding the GitHub App private key + webhook HMAC secret.
# These are the ONLY non-Azure secret (Article IX); they are read at runtime by the api/mcp UAMIs (Key
# Vault Secrets User) as ACA Key Vault-backed secrets — no value in app config.
#
# SECRET VALUES ARE NOT DECLARED HERE. The AVM module writes any `secrets_value` straight into tofu state;
# to keep the secret value OUT of state (SC-007 / T061) we provision the vault + RBAC ONLY and seed the two
# secret values out-of-band via the secure pipeline path (`az keyvault secret set`, runbook step 2). The
# ACA apps reference them by the constructed versionless URI (local.kv_secret_uri_*), which ACA resolves to
# the latest version at runtime through the app UAMI.
#
# RBAC is the module default (no `legacy_access_policies_enabled`); there is deliberately no
# `enable_rbac_authorization` argument (the module does not expose one).
# ---------------------------------------------------------------------------

module "key_vault" {
  source  = "Azure/avm-res-keyvault-vault/azurerm"
  version = "0.10.2"

  name                = local.key_vault_name
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location
  tenant_id           = data.azurerm_client_config.current.tenant_id

  sku_name = "standard"

  # NETWORK POSTURE — OWNER DECISION before the live deploy (T026/T039): this sets RBAC-gated access over
  # the public network path (network_acls = null disables the module's default deny-all firewall, which
  # would otherwise block ACA from resolving the secrets). Data-plane access is still restricted to the two
  # UAMIs by RBAC (Key Vault Secrets User). Per Article IX ("no public endpoint unless a spec demands one")
  # the tighter alternative is a PRIVATE ENDPOINT into the control-plane VNet (needs a
  # privatelink.vaultcore.azure.net zone in the dns RG) — deferred because research §8/§11 specified only
  # "Standard Key Vault + UAMI access", not network isolation. Revisit at T026 if the owner wants the PE.
  network_acls = null

  # Grant Key Vault Secrets User (data-plane read) to the two Postgres/secret-touching app UAMIs. The
  # ingress UAMI gets NO Key Vault access (it forwards only; data-model §2).
  role_assignments = {
    uami_api_secrets_user = {
      role_definition_id_or_name = "Key Vault Secrets User"
      principal_id               = azurerm_user_assigned_identity.api.principal_id
      principal_type             = "ServicePrincipal"
    }
    uami_mcp_secrets_user = {
      role_definition_id_or_name = "Key Vault Secrets User"
      principal_id               = azurerm_user_assigned_identity.mcp.principal_id
      principal_type             = "ServicePrincipal"
    }
  }

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# T017 — Log Analytics workspace (PerGB2018, ~1 GB/day cap). Required by the ACA managed environment and
# the workspace-based Application Insights (T048). Daily cap + 30-day retention keep it cheap (Article IX).
# ---------------------------------------------------------------------------

module "log_analytics" {
  source  = "Azure/avm-res-operationalinsights-workspace/azurerm"
  version = "0.5.1"

  name                = local.law_name
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location

  log_analytics_workspace_sku               = "PerGB2018"
  log_analytics_workspace_retention_in_days = 30
  log_analytics_workspace_daily_quota_gb    = 1

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# T048 (US4) — Application Insights (workspace-based) — the env_id-correlated telemetry sink.
# The spec-006 verb layer ALREADY emits OpenTelemetry traces/metrics stamped with env_id
# (ControlPlaneTelemetry, spec-006 T022); this stack just provisions the sink and the api/mcp apps export
# to it via UseAzureMonitor() when APPLICATIONINSIGHTS_CONNECTION_STRING is set (T049). Workspace-based:
# telemetry lands in the T017 Log Analytics workspace (workspace_id), which already carries the daily cap,
# so there is no second cost surface (Article IX). application_type "web" = standard ASP.NET Core app.
# ---------------------------------------------------------------------------

module "application_insights" {
  source  = "Azure/avm-res-insights-component/azurerm"
  version = "0.4.0"

  name                = local.appinsights_name
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location

  application_type = "web"
  workspace_id     = module.log_analytics.resource_id # workspace-based: data flows into the LAW (T017)

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# T018 — ACA managed environment (workload-profiles, VNet-integrated, EXTERNAL).
# EXTERNAL (vnet_configuration.internal = false) so the environment has a public IP — required so GitHub
# can reach the webhook via the ingress app. Per-app ingress visibility is then independent: the ingress
# app is external, api/mcp are internal (research §4). Injected into the existing ACA subnet (data source);
# same VNet as Postgres ⇒ the existing privatelink.postgres DNS link resolves the ledger (research §3).
# ---------------------------------------------------------------------------

# Pinned to 0.4.0, NOT 0.5.0: v0.5.0's `managed_certificates` + `storages` submodules declare
# required_version `~> 1.12`, which fails under our constitution-pinned OpenTofu 1.11.x (Article V smoke
# finding, T009). v0.4.0 allows `>= 1.10, < 2.0` across root + submodules. Its input surface differs from
# 0.5.0: a flat `infrastructure_subnet_id` (no `vnet_configuration` object and no `internal` flag — the
# environment defaults to EXTERNAL/public-LB with a subnet, exactly what we need); `workload_profile`
# (singular set) not `workload_profiles`; `zone_redundancy_enabled` not `zone_redundant`.
module "managed_environment" {
  source  = "Azure/avm-res-app-managedenvironment/azurerm"
  version = "0.4.0"

  name                = local.aca_env_name
  resource_group_name = azurerm_resource_group.host.name
  location            = azurerm_resource_group.host.location

  log_analytics_workspace = {
    resource_id = module.log_analytics.resource_id
  }

  # VNet injection into the existing ACA subnet (data source). With a subnet and no internal-LB flag the
  # environment is EXTERNAL (public IP) — required so GitHub reaches the webhook via the ingress app; the
  # api/mcp apps stay internal via their own ingress.external_enabled = false (research §4).
  infrastructure_subnet_id = data.azurerm_subnet.aca.id

  workload_profile = [{
    name                  = "Consumption"
    workload_profile_type = "Consumption"
  }]

  zone_redundancy_enabled = false # single-AZ control plane (Article IX — smallest viable)
  enable_telemetry        = false
  tags                    = local.tags
}

# ---------------------------------------------------------------------------
# T019 — uami-api: subscription Reader (Azure Resource Graph reconcile reads). The reconciler/inventory
# reads "what's deployed" from ARG (Article III); Reader at the platform subscription satisfies it. A
# custom Resource-Graph-Reader role is a tighter optional alternative (data-model §2).
# Article V: role assignments are first-class provider resources (no AVM module) — justified in README.md.
# ---------------------------------------------------------------------------

resource "azurerm_role_assignment" "api_reader" {
  scope                = "/subscriptions/${var.platform_subscription_id}"
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.api.principal_id
  principal_type       = "ServicePrincipal"
}

# ---------------------------------------------------------------------------
# T020 — the `api` container app (INTERNAL ingress, always-on min replicas 1).
# Hosts the spec-006 Api: the workflow_run webhook sink + Wolverine durable inbox/outbox + the polling
# reconciler (research §13 — the Api is the SOLE tracking node). Internal-only; reached by the YARP ingress
# at /webhooks/github. Postgres via uami-api Entra token (no password); GitHub App key + webhook secret as
# Key Vault-backed secrets resolved by uami-api.
# ---------------------------------------------------------------------------

module "container_app_api" {
  source  = "Azure/avm-res-app-containerapp/azurerm"
  version = "0.9.0"

  name                                  = local.app_name_api
  resource_group_name                   = azurerm_resource_group.host.name
  resource_group_id                     = azurerm_resource_group.host.id
  location                              = azurerm_resource_group.host.location
  container_app_environment_resource_id = module.managed_environment.resource_id

  revision_mode         = "Single"
  workload_profile_name = "Consumption"

  managed_identities = {
    user_assigned_resource_ids = [azurerm_user_assigned_identity.api.id]
  }

  # Credential-free image pull via uami-api (AcrPull granted in T015).
  registries = [{
    server   = module.acr.resource.login_server
    identity = azurerm_user_assigned_identity.api.id
  }]

  # Key Vault-backed secrets — value resolved at runtime by uami-api; never in state (SC-007). `identity`
  # is mandatory alongside `key_vault_secret_id` (module validation).
  secrets = {
    gh_app_key = {
      name                = local.secret_name_gh_app_key
      key_vault_secret_id = local.kv_secret_uri_gh_app_key
      identity            = azurerm_user_assigned_identity.api.id
    }
    gh_webhook = {
      name                = local.secret_name_gh_webhook
      key_vault_secret_id = local.kv_secret_uri_gh_webhook
      identity            = azurerm_user_assigned_identity.api.id
    }
  }

  ingress = {
    external_enabled = false # internal — the YARP ingress app is the only public surface (Article IX)
    target_port      = 8080  # dotnet/aspnet:10.0 default ASPNETCORE_HTTP_PORTS
    transport        = "auto"
    traffic_weight = [{
      latest_revision = true
      percentage      = 100
    }]
  }

  template = {
    min_replicas = 1 # always-on: the webhook sink + reconciler must always be live (research §13)
    max_replicas = 3
    containers = [{
      name   = "api"
      image  = "${module.acr.resource.login_server}/${var.api_image_repository}:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"
      env = concat([
        { name = "ControlPlane__PostgresConnectionString", value = local.postgres_connection_string_api },
        { name = "ControlPlane__PlatformSubscriptionId", value = var.platform_subscription_id },
        { name = "ManagedIdentity__ClientId", value = azurerm_user_assigned_identity.api.client_id },
        # env_id-correlated telemetry → App Insights (T049). The verb layer's UseAzureMonitor() reads this
        # key; empty/unset is a graceful no-op (spec edge case, T051). Azure-generated ingestion credential
        # (not a non-Azure secret) so it rides as a plain env — SC-007 scopes state-secrecy to the GitHub
        # App key + webhook secret + registry password, none of which this is.
        { name = "APPLICATIONINSIGHTS_CONNECTION_STRING", value = module.application_insights.connection_string },
        { name = "GitHubApp__PrivateKeyPem", secret_name = local.secret_name_gh_app_key },
        { name = "GitHubApp__WebhookSecret", secret_name = local.secret_name_gh_webhook },
        ], [
        for k, v in local.github_app_env : { name = k, value = v }
      ])
    }]
  }

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# T021 — the `ingress` container app (EXTERNAL ingress, always-on min replicas 1).
# The single public surface (Article IX): YARP reverse proxy forwarding POST /webhooks/github to the
# internal api (and, from US2/T037, /mcp + the PRM doc to the internal mcp). Holds NO data-plane credential
# — uami-ingress has AcrPull ONLY. The route destinations are set by env (see src/...Ingress/appsettings).
# ---------------------------------------------------------------------------

module "container_app_ingress" {
  source  = "Azure/avm-res-app-containerapp/azurerm"
  version = "0.9.0"

  name                                  = local.app_name_ingress
  resource_group_name                   = azurerm_resource_group.host.name
  resource_group_id                     = azurerm_resource_group.host.id
  location                              = azurerm_resource_group.host.location
  container_app_environment_resource_id = module.managed_environment.resource_id

  revision_mode         = "Single"
  workload_profile_name = "Consumption"

  managed_identities = {
    user_assigned_resource_ids = [azurerm_user_assigned_identity.ingress.id]
  }

  registries = [{
    server   = module.acr.resource.login_server
    identity = azurerm_user_assigned_identity.ingress.id
  }]

  ingress = {
    external_enabled = true # the ONLY public app
    target_port      = 8080
    transport        = "auto"
    traffic_weight = [{
      latest_revision = true
      percentage      = 100
    }]
  }

  template = {
    min_replicas = 1 # always-on so GitHub webhooks always have a public target
    max_replicas = 3
    containers = [{
      name   = "ingress"
      image  = "${module.acr.resource.login_server}/${var.ingress_image_repository}:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"
      # YARP cluster destinations point at the internal api/mcp app FQDNs (set here so one image serves all
      # environments). Route shapes live in appsettings.json (the public surface contract).
      # fqdn_url is the app's full https URL (the v0.9.0 module has no bare `fqdn` output). The api/mcp apps
      # are internal, so these resolve to their internal environment FQDNs — reachable app-to-app in the env.
      env = [
        { name = "ReverseProxy__Clusters__control-plane-api__Destinations__api__Address", value = "${module.container_app_api.fqdn_url}/" },
        { name = "ReverseProxy__Clusters__control-plane-mcp__Destinations__mcp__Address", value = "${module.container_app_mcp.fqdn_url}/" },
      ]
    }]
  }

  enable_telemetry = false
  tags             = local.tags
}

# ---------------------------------------------------------------------------
# T036 — uami-mcp: subscription Reader (Azure Resource Graph reads for the inventory verbs the mcp host
# serves in-process). Mirrors the uami-api Reader grant (T019); the mcp host runs the verb layer exactly
# as the CLI does, so it needs the same ARG read scope (data-model §2).
# Article V: role assignments are first-class provider resources (no AVM module) — justified in README.md.
# ---------------------------------------------------------------------------

resource "azurerm_role_assignment" "mcp_reader" {
  scope                = "/subscriptions/${var.platform_subscription_id}"
  role_definition_name = "Reader"
  principal_id         = azurerm_user_assigned_identity.mcp.principal_id
  principal_type       = "ServicePrincipal"
}

# ---------------------------------------------------------------------------
# T035 — the `mcp` container app (INTERNAL ingress, SCALE-TO-ZERO min replicas 0).
# Hosts the spec-006 verb layer in-process (like the `pdp` CLI) and exposes it as Entra-gated MCP tools over
# stateless streamable HTTP (research §13 — NOT a tracking node: Serverless durability, no reconciler).
# Internal-only; reached by the YARP ingress at /mcp + /.well-known/oauth-protected-resource (T037). Postgres
# via uami-mcp Entra token (no password); GitHub App key as a Key Vault-backed secret resolved by uami-mcp
# (no webhook secret — the mcp app does not handle webhooks; data-model §2). The MCP endpoint validates Entra
# JWTs itself (AzureAd config) and authorizes the single owner oid.
# ---------------------------------------------------------------------------

module "container_app_mcp" {
  source  = "Azure/avm-res-app-containerapp/azurerm"
  version = "0.9.0"

  name                                  = local.app_name_mcp
  resource_group_name                   = azurerm_resource_group.host.name
  resource_group_id                     = azurerm_resource_group.host.id
  location                              = azurerm_resource_group.host.location
  container_app_environment_resource_id = module.managed_environment.resource_id

  revision_mode         = "Single"
  workload_profile_name = "Consumption"

  managed_identities = {
    user_assigned_resource_ids = [azurerm_user_assigned_identity.mcp.id]
  }

  # Credential-free image pull via uami-mcp (AcrPull granted in T015).
  registries = [{
    server   = module.acr.resource.login_server
    identity = azurerm_user_assigned_identity.mcp.id
  }]

  # Key Vault-backed GitHub App key only (no webhook secret) — resolved at runtime by uami-mcp; never in
  # state (SC-007).
  secrets = {
    gh_app_key = {
      name                = local.secret_name_gh_app_key
      key_vault_secret_id = local.kv_secret_uri_gh_app_key
      identity            = azurerm_user_assigned_identity.mcp.id
    }
  }

  ingress = {
    external_enabled = false # internal — the YARP ingress app is the only public surface (Article IX)
    target_port      = 8080  # dotnet/aspnet:10.0 default ASPNETCORE_HTTP_PORTS
    transport        = "auto"
    traffic_weight = [{
      latest_revision = true
      percentage      = 100
    }]
  }

  template = {
    min_replicas = 0 # scale-to-zero: the conversational surface is cold until called (research §13)
    max_replicas = 3
    containers = [{
      name   = "mcp"
      image  = "${module.acr.resource.login_server}/${var.mcp_image_repository}:${var.image_tag}"
      cpu    = 0.25
      memory = "0.5Gi"
      env = concat([
        { name = "ControlPlane__PostgresConnectionString", value = local.postgres_connection_string_mcp },
        { name = "ControlPlane__PlatformSubscriptionId", value = var.platform_subscription_id },
        { name = "ManagedIdentity__ClientId", value = azurerm_user_assigned_identity.mcp.client_id },
        # env_id-correlated telemetry → App Insights (T049 — no longer deferred). UseAzureMonitor() reads
        # this key; empty/unset is a graceful no-op (T051). Azure ingestion credential, plain env (SC-007).
        { name = "APPLICATIONINSIGHTS_CONNECTION_STRING", value = module.application_insights.connection_string },
        { name = "GitHubApp__PrivateKeyPem", secret_name = local.secret_name_gh_app_key },
        # The MCP endpoint's OAuth 2.1 protected-resource config (single-owner authz; data-model §3).
        # TenantId is the deploy tenant (the data source) — never a passable var, so it can't be left empty
        # (an empty tenant builds a malformed authority and rejects every token). Audience = the app reg
        # appId (v2 `aud`); OwnerOid = the single allow-listed owner.
        { name = "AzureAd__TenantId", value = data.azurerm_client_config.current.tenant_id },
        { name = "AzureAd__Audience", value = local.mcp_audience },
        { name = "AzureAd__OwnerOid", value = var.owner_object_id },
        ], [
        for k, v in local.github_app_env : { name = k, value = v }
      ])
    }]
  }

  enable_telemetry = false
  tags             = local.tags
}
