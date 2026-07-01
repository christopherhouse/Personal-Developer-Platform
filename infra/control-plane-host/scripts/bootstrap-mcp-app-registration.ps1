#Requires -Version 7.0
<#
.SYNOPSIS
  Create the Entra app registration that backs the MCP endpoint's OAuth 2.1 audience (api://pdp-mcp),
  so owner tokens can be minted and the MCP server's JWT validation (aud + v2 issuer + oid) succeeds.
  One-time owner identity bootstrap (analogous to the Postgres principal bootstrap, SC-010). Idempotent.

.DESCRIPTION
  The host stack pins AzureAd__Audience = api://pdp-mcp on the mcp container app and validates incoming
  JWTs against it, but it does NOT create the app registration (an Entra directory object, not infra —
  it would need a standing Entra-write CI credential, which we avoid). The owner creates it once here.

  Steps: create the app (api://pdp-mcp) → v2 access tokens (matches the server's v2.0 ValidIssuer) →
  service principal → pre-authorize the Azure CLI public client on the exposed `mcp:tools` scope so
  `az account get-access-token --resource api://pdp-mcp` works with no consent prompt → register the
  MCP Inspector SPA redirect URIs. The Graph PATCH reads-modifies-writes the whole `api` object so the
  exposed scope is preserved.

  Requires: az login as an identity allowed to create app registrations (Application Developer / Cloud
  Application Administrator, or a tenant that permits user app registration).

.EXAMPLE
  az login
  ./scripts/bootstrap-mcp-app-registration.ps1
  # then mint a token:
  az account get-access-token --resource api://pdp-mcp --query accessToken -o tsv
#>
[CmdletBinding()]
param(
  [string] $Audience = 'api://pdp-mcp',
  [string] $DisplayName = 'pdp-mcp'
)

$ErrorActionPreference = 'Continue'   # az writes warnings to stderr; 'Stop' turns them into terminating errors.
$AzCliClientId = '04b07795-8ddb-461a-bbee-02f9e1bf7b46'   # the well-known Azure CLI public client

function Write-Step($m) { Write-Host ">> $m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "[OK] $m" -ForegroundColor Green }
function Die($m) { Write-Host "[X] $m" -ForegroundColor Red; exit 1 }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { Die 'az not found on PATH' }

Write-Step "Confirming sign-in..."
$me = az ad signed-in-user show --query userPrincipalName -o tsv
if ($LASTEXITCODE -ne 0) { Die 'not signed in - run: az login' }
Write-Ok "Signed in as: $me"

Write-Step "Finding or creating the app registration ($Audience)..."
# Match by identifier URI first, then by display name — a prior run may have created the app but failed
# before the URI was set (so the URI lookup would miss it and we'd create a duplicate).
$appId = az ad app list --identifier-uri $Audience --query "[0].appId" -o tsv
if (-not $appId) {
  $appId = az ad app list --display-name $DisplayName --query "[0].appId" -o tsv
}
if (-not $appId) {
  $appId = az ad app create --display-name $DisplayName --sign-in-audience AzureADMyOrg --query appId -o tsv
  if ($LASTEXITCODE -ne 0 -or -not $appId) { Die 'app registration create failed' }
  Write-Ok "Created app $DisplayName ($appId)"
  Start-Sleep -Seconds 10   # let the new app replicate before the updates below
}
else {
  Write-Ok "Reusing existing app $DisplayName ($appId)"
}
$objectId = az ad app show --id $appId --query id -o tsv
if (-not $objectId) { Die 'could not resolve the app object id' }

Write-Step "Ensuring a service principal exists in the tenant..."
az ad sp show --id $appId --query id -o tsv 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
  az ad sp create --id $appId | Out-Null
  Write-Ok "Service principal created"
}

# v2 tokens MUST be set BEFORE the identifier URI: a strict tenant (e.g. MCAPS) rejects a bare
# `api://pdp-mcp` URI ("must contain a verified domain, tenant id, or app id") UNLESS the app issues
# v2 access tokens. Read-modify-write the WHOLE api object so any existing exposed scope is preserved
# (a partial PATCH of `api` replaces the complex type and would drop the scope).
# PATCH the app's `api` block. Two passes are REQUIRED: Graph validates
# preAuthorizedApplications.delegatedPermissionIds against the ALREADY-PERSISTED scopes, so a brand-new
# scope and its pre-authorization cannot land in one request. Pass 1 creates the scope + v2; pass 2 adds
# the pre-authorization. Always send the WHOLE api object (a partial PATCH of the complex type drops fields).
function Invoke-ApiPatch($apiObj) {
  $body = [pscustomobject]@{ api = $apiObj } | ConvertTo-Json -Depth 12
  $tmp = [IO.Path]::GetTempFileName()
  [IO.File]::WriteAllText($tmp, $body, (New-Object Text.UTF8Encoding($false)))
  try {
    az rest --method PATCH `
      --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
      --headers 'Content-Type=application/json' --body "@$tmp" | Out-Null
    return $LASTEXITCODE
  }
  finally { Remove-Item $tmp -ErrorAction SilentlyContinue }
}

Write-Step "Pass 1: v2 access tokens + exposing a delegated scope..."
$api = az ad app show --id $appId --query api -o json | ConvertFrom-Json
# Reuse an existing delegated scope, or CREATE one — current `az ad app create` does not auto-add
# user_impersonation. The scope id is what the Azure CLI pre-authorization (and `.default`) references.
if (@($api.oauth2PermissionScopes).Count -ge 1 -and $api.oauth2PermissionScopes[0].id) {
  $scopeId = $api.oauth2PermissionScopes[0].id
}
else {
  $scopeId = (New-Guid).Guid
  # Scope value MUST be `mcp:tools`: the MCP server publishes it in the protected-resource-metadata
  # document (ScopesSupported) and the WWW-Authenticate challenge, so an OAuth client (e.g. MCP Inspector)
  # requests `api://pdp-mcp/mcp:tools`. Entra rejects a mismatched scope as invalid_scope. The server
  # validates aud + v2 issuer + oid (NOT scp), so the value only has to match the published contract.
  $api.oauth2PermissionScopes = @(
    [pscustomobject]@{
      id                      = $scopeId
      value                   = 'mcp:tools'
      type                    = 'User'
      isEnabled               = $true
      adminConsentDisplayName = 'Call pdp-mcp tools as the signed-in owner'
      adminConsentDescription = 'Allow calling the pdp-mcp MCP tools as the signed-in owner.'
      userConsentDisplayName  = 'Call pdp-mcp tools'
      userConsentDescription  = 'Allow calling the pdp-mcp MCP tools on your behalf.'
    }
  )
}
$api.requestedAccessTokenVersion = 2
if ((Invoke-ApiPatch $api) -ne 0) { Die 'Graph PATCH pass 1 (scope + v2) failed' }
Start-Sleep -Seconds 5   # let the scope + v2 flip persist before referencing them

Write-Step "Pass 2: pre-authorizing the Azure CLI client on the scope..."
$api.preAuthorizedApplications = @(
  [pscustomobject]@{ appId = $AzCliClientId; delegatedPermissionIds = @($scopeId) }
)
if ((Invoke-ApiPatch $api) -ne 0) { Die 'Graph PATCH pass 2 (pre-authorize) failed' }
Start-Sleep -Seconds 5   # let it replicate before the URI policy is re-evaluated

# Now set the identifier URI. Try the clean name first; if the tenant policy still refuses it, fall
# back to api://<appId> (always allowed — it contains the app id). The fallback CHANGES the audience.
Write-Step "Setting the Application ID URI to $Audience..."
az ad app update --id $appId --identifier-uris $Audience 2>$null | Out-Null
if ($LASTEXITCODE -ne 0) {
  $fallback = "api://$appId"
  Write-Host "  Tenant policy refused '$Audience'; falling back to '$fallback'." -ForegroundColor Yellow
  az ad app update --id $appId --identifier-uris $fallback | Out-Null
  if ($LASTEXITCODE -ne 0) { Die "could not set an identifier URI ('$Audience' or '$fallback')" }
  $Audience = $fallback
}

Write-Step "Registering the MCP Inspector redirect URIs (SPA platform)..."
# MCP Inspector is a browser app that performs cross-origin PKCE token redemption at
# http://localhost:6274/oauth/callback. Entra only permits cross-origin redemption for the
# Single-Page Application platform (else AADSTS9002326), so the URIs go under `spa`, NOT web/public.
# The server side is unaffected — it just validates the resulting JWT (aud + v2 issuer + oid).
$spaBody = [pscustomobject]@{
  spa = [pscustomobject]@{
    redirectUris = @(
      'http://localhost:6274/oauth/callback'
      'http://127.0.0.1:6274/oauth/callback'
    )
  }
} | ConvertTo-Json -Depth 8
$spaTmp = [IO.Path]::GetTempFileName()
[IO.File]::WriteAllText($spaTmp, $spaBody, (New-Object Text.UTF8Encoding($false)))
try {
  az rest --method PATCH `
    --uri "https://graph.microsoft.com/v1.0/applications/$objectId" `
    --headers 'Content-Type=application/json' --body "@$spaTmp" | Out-Null
  if ($LASTEXITCODE -ne 0) { Die 'Graph PATCH (SPA redirect URIs) failed' }
}
finally { Remove-Item $spaTmp -ErrorAction SilentlyContinue }
Write-Ok 'SPA redirect URIs registered for MCP Inspector'

Write-Ok "App registration ready. Audience = $Audience  (appId $appId)"
Write-Host ""
if ($Audience -ne 'api://pdp-mcp') {
  Write-Host "!! The audience is NOT api://pdp-mcp (tenant policy forced the app-id form)." -ForegroundColor Red
  Write-Host "   The mcp container validates AzureAd__Audience=api://pdp-mcp, so it will 401 until you" -ForegroundColor Red
  Write-Host "   update it to: $Audience  (host stack mcp container env, then restart the revision)." -ForegroundColor Red
  Write-Host ""
}
Write-Host "Mint an owner token with:" -ForegroundColor Yellow
Write-Host "  az account get-access-token --resource $Audience --query accessToken -o tsv"
Write-Host "(If it says consent required, run once interactively: az login --scope $Audience/.default)"
