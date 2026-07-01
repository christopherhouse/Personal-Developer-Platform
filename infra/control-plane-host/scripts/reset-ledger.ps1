#Requires -Version 5.1
<#
.SYNOPSIS
  DESTRUCTIVE dev tool: reset the private control-plane ledger (ipam) + registry (environments/runs) to a
  clean slate, in-VNet, as the Entra admin. Applies reset-ledger.sql via a transient Manual ACA Job in the
  ledger's VNet -- the exact mechanism of run-migrations.ps1 (the private Postgres is unreachable from a
  laptop). YOU stay on your laptop; az is a control-plane call; the psql runs in the Job; it self-deletes.

.DESCRIPTION
  Keeps the seeded baseline + real region pools; drops all spoke allocations + the phantom swedencentral
  pool; wipes the entire registry run/env history. See reset-ledger.sql for the exact statements and adjust
  the region filter there if your topology differs.

  DESTROY ANY REAL SPOKES IN AZURE FIRST (spoke-destroy.yml). Deleting ledger rows while the Azure
  resources still exist creates the worse drift (live resources with no ledger record + re-allocatable CIDR).

  Requires -Force to run (guard against an accidental wipe). Requires: az login as the Postgres Entra admin.

.EXAMPLE
  az login                 # as the Postgres Entra admin
  ./scripts/reset-ledger.ps1 -Force
#>
[CmdletBinding()]
param(
  [switch] $Force,
  [string] $SqlPath = "$PSScriptRoot/reset-ledger.sql",
  [string] $PgBootImage = 'postgres:16-alpine'
)

$ErrorActionPreference = 'Continue'   # az writes warnings to stderr; 'Stop' turns them into terminating errors.

function Write-Step($m) { Write-Host ">> $m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "[OK] $m" -ForegroundColor Green }
function Die($m) { Write-Host "[X] $m" -ForegroundColor Red; exit 1 }

if (-not $Force) {
  Die "refusing to run without -Force. This WIPES all spoke allocations + the entire registry env/run history. Destroy real spokes in Azure first, then re-run with -Force."
}
foreach ($t in @('az', 'tofu')) { if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { Die "$t not found on PATH" } }
if (-not (Test-Path -LiteralPath $SqlPath)) { Die "missing SQL file: $SqlPath" }

# Operate on THIS stack regardless of caller cwd; $PSScriptRoot is .../scripts, the stack is its parent.
Set-Location -Path (Join-Path $PSScriptRoot '..')

Write-Step 'Reading stack coordinates from tofu output...'
$ctxJson = tofu output -json bootstrap_context 2>$null
if ($LASTEXITCODE -ne 0 -or -not $ctxJson) {
  Write-Step 'tofu backend not initialized here - running "tofu init -reconfigure" (read-only)...'
  tofu init -reconfigure -input=false | Out-Null
  $ctxJson = tofu output -json bootstrap_context 2>$null
}
if (-not $ctxJson) { Die 'no "bootstrap_context" output - the host stack is NOT applied here' }
$ctx = $ctxJson | ConvertFrom-Json
$RG = $ctx.resource_group; $EnvId = $ctx.aca_environment_id; $PgFqdn = $ctx.ledger_fqdn; $PgDb = $ctx.postgres_database
if (-not ($RG -and $EnvId -and $PgFqdn -and $PgDb)) { Die "incomplete bootstrap_context (rg=$RG env=$EnvId host=$PgFqdn db=$PgDb)" }
$Location = az group show -n $RG --query location -o tsv
Write-Ok "rg=$RG db=$PgDb host=$PgFqdn"

Write-Step 'Confirming sign-in (must be the Postgres Entra admin)...'
$Upn = az account show --query user.name -o tsv
if ($LASTEXITCODE -ne 0) { Die 'not signed in - run: az login' }
Write-Ok "Signed in as: $Upn  (this identity MUST be the ledger's Entra admin)"

Write-Step 'Minting oss-rdbms access token...'
$Token = az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv
if ($LASTEXITCODE -ne 0) { Die 'could not get an oss-rdbms token' }

$SqlB64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($SqlPath))

$job = 'caj-pgreset'
Write-Step "Cleaning any leftover job '$job' (idempotent)..."
az containerapp job delete -n $job -g $RG --yes 2>$null | Out-Null

Write-Step "Creating transient in-VNet ACA Job '$job' (runs reset-ledger.sql against '$PgDb')..."
# The backtick before $VARs keeps them LITERAL in YAML - expanded by the container's /bin/sh at runtime.
$yaml = @"
location: $Location
properties:
  environmentId: $EnvId
  configuration:
    triggerType: Manual
    replicaTimeout: 600
    replicaRetryLimit: 0
    manualTriggerConfig:
      parallelism: 1
      replicaCompletionCount: 1
    secrets:
      - name: pgtoken
        value: "$Token"
  template:
    containers:
      - name: pgreset
        image: $PgBootImage
        resources:
          cpu: 0.5
          memory: 1.0Gi
        command: ["/bin/sh", "-c"]
        args:
          - 'set -e; echo "Applying reset..."; echo "`$SQL_B64" | base64 -d | psql -v ON_ERROR_STOP=1 -f -; echo "Reset applied."'
        env:
          - name: PGHOST
            value: "$PgFqdn"
          - name: PGPORT
            value: "5432"
          - name: PGDATABASE
            value: "$PgDb"
          - name: PGUSER
            value: "$Upn"
          - name: PGSSLMODE
            value: "require"
          - name: PGPASSWORD
            secretRef: pgtoken
          - name: SQL_B64
            value: "$SqlB64"
"@
$yamlPath = [IO.Path]::GetTempFileName()
# UTF-8 WITHOUT BOM on PS 5.1 and 7 (5.1's Set-Content -Encoding utf8 writes a BOM that az --yaml rejects).
[IO.File]::WriteAllText($yamlPath, $yaml, (New-Object Text.UTF8Encoding($false)))
try {
  az containerapp job create -n $job -g $RG --yaml $yamlPath | Out-Null
  if ($LASTEXITCODE -ne 0) { Die 'job create failed' }
}
finally { Remove-Item $yamlPath -ErrorAction SilentlyContinue }

Write-Step 'Starting the Job...'
$exec = az containerapp job start -n $job -g $RG --query name -o tsv
if ($LASTEXITCODE -ne 0) { Die 'job start failed' }

Write-Step "Waiting for execution '$exec'..."
$status = ''
for ($i = 0; $i -lt 120; $i++) {
  $status = az containerapp job execution list -n $job -g $RG --query "[?name=='$exec'].properties.status | [0]" -o tsv 2>$null
  if ($status -in @('Succeeded', 'Failed')) { break }
  Start-Sleep -Seconds 5
}

Write-Step 'Job logs:'
az containerapp job logs show -n $job -g $RG --container pgreset --execution $exec --tail 200 2>$null

if ($status -eq 'Succeeded') {
  Write-Ok 'reset applied (execution Succeeded).'
  az containerapp job delete -n $job -g $RG --yes 2>$null | Out-Null
  Write-Ok 'transient job deleted.'
}
else {
  $shownStatus = if ($status) { $status } else { 'unknown' }
  Write-Host "[X] execution status: $shownStatus - job '$job' left for inspection; delete with: az containerapp job delete -n $job -g $RG --yes" -ForegroundColor Red
  Die 'reset failed'
}
