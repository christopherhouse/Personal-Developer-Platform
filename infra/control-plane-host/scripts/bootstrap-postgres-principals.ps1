#Requires -Version 5.1
<#
.SYNOPSIS
  The SC-010 "single reviewed manual step", automated as a transient in-VNet ACA Job (PowerShell port of
  bootstrap-postgres-principals.sh). Registers the per-app UAMIs (uami-api / uami-mcp) as Postgres Entra
  principals + grants them on ipam/registry, so the apps' token logins succeed (spec 007 research sec 6).

.DESCRIPTION
  The ledger Postgres is private (VNet-injected) + Entra-only, and pgaadauth_* must run as the Entra ADMIN.
  This stack's ACA managed environment is already in that VNet, so we run psql there - as a short-lived
  Manual ACA Job - while YOU stay on your laptop. az is a control-plane call; the psql happens inside the
  Job, in-VNet. Your oss-rdbms token (minted as the Entra admin) rides in as a Job SECRET and is the psql
  "password". The Job is deleted as soon as it finishes. Idempotent - safe to re-run.

  NOTE: this file is intentionally ASCII-only so it parses identically under Windows PowerShell 5.1
  (ANSI default) and PowerShell 7 (UTF-8) - do not add non-ASCII glyphs.

.PARAMETER Targets
  Which principals to bootstrap: api, mcp, or both (default).

.PARAMETER PgBootImage
  psql image. Public Docker Hub by default; override if Docker Hub pulls are blocked (e.g. a mirror in ACR).

.EXAMPLE
  az login                 # as the Postgres Entra ADMIN
  cd infra/control-plane-host
  ./scripts/bootstrap-postgres-principals.ps1            # both api + mcp

.EXAMPLE
  ./scripts/bootstrap-postgres-principals.ps1 -Targets mcp
#>
[CmdletBinding()]
param(
  [ValidateSet('api', 'mcp')]
  [string[]] $Targets = @('api', 'mcp'),
  [string] $PgBootImage = 'postgres:16-alpine'
)

$ErrorActionPreference = 'Stop'

function Write-Step($m) { Write-Host ">> $m" -ForegroundColor Cyan }
function Write-Ok($m) { Write-Host "[OK] $m" -ForegroundColor Green }
function Die($m) { Write-Host "[X] $m" -ForegroundColor Red; exit 1 }
# Native-exe failures don't trip $ErrorActionPreference - check $LASTEXITCODE explicitly.
function Assert-LastExit($m) { if ($LASTEXITCODE -ne 0) { Die $m } }

foreach ($t in @('az', 'tofu')) {
  if (-not (Get-Command $t -ErrorAction SilentlyContinue)) { Die "$t not found on PATH" }
}

Write-Step 'Reading stack coordinates from tofu output...'
$ctxJson = tofu output -json bootstrap_context
Assert-LastExit "could not read 'bootstrap_context' output - apply the stack first, and run from infra/control-plane-host"
$ctx = $ctxJson | ConvertFrom-Json
$RG = $ctx.resource_group
$EnvId = $ctx.aca_environment_id
$PgFqdn = $ctx.ledger_fqdn
$PgDb = $ctx.postgres_database
if (-not ($RG -and $EnvId -and $PgFqdn -and $PgDb)) {
  Die "incomplete bootstrap_context (rg=$RG env=$EnvId host=$PgFqdn db=$PgDb)"
}

$Location = az group show -n $RG --query location -o tsv
Assert-LastExit "could not resolve location for RG $RG"

Write-Step 'Confirming Azure sign-in (must be the Postgres Entra admin)...'
$Upn = az account show --query user.name -o tsv
Assert-LastExit 'not signed in - run: az login'
Write-Ok "Signed in as: $Upn  (this identity MUST be the ledger's Entra admin)"

Write-Step 'Minting oss-rdbms access token...'
$Token = az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv
Assert-LastExit 'could not get an oss-rdbms token'

function Invoke-BootstrapOne([string] $Name) {
  $outName = "pgaadauth_bootstrap_uami_$Name"
  $job = "caj-pgboot-$Name"

  Write-Step "[$Name] fetching the bootstrap SQL from tofu output '$outName'..."
  $sql = tofu output -raw $outName
  Assert-LastExit "[$Name] no output '$outName'"
  # base64 (single line, no special chars) sidesteps all YAML/CLI quoting of multi-line SQL.
  $sqlB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($sql))

  Write-Host "`n------ SQL that will run for $Name (review) ------" -ForegroundColor DarkGray
  Write-Host $sql
  Write-Host "------------------------------------------------`n" -ForegroundColor DarkGray

  # Clean any leftover job from a previous run (idempotent); ignore "not found".
  az containerapp job delete -n $job -g $RG --yes 2>$null | Out-Null

  Write-Step "[$Name] creating transient in-VNet ACA Job '$job'..."
  # NOTE: the backtick before $SQL_B64 keeps it LITERAL in the YAML - it is expanded by the container's
  # /bin/sh at runtime (from the SQL_B64 env var), not by PowerShell.
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
      - name: pgboot
        image: $PgBootImage
        resources:
          cpu: 0.25
          memory: 0.5Gi
        command: ["/bin/sh", "-c"]
        args:
          - 'echo "`$SQL_B64" | base64 -d | psql -v ON_ERROR_STOP=1 -f -'
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
            value: "$sqlB64"
"@
  $yamlPath = [System.IO.Path]::GetTempFileName()
  # UTF-8 WITHOUT BOM on both PS 5.1 and 7. (5.1's `Set-Content -Encoding utf8` writes a BOM, which
  # `az containerapp job create --yaml` rejects; this .NET call is BOM-free on every version.)
  [System.IO.File]::WriteAllText($yamlPath, $yaml, (New-Object System.Text.UTF8Encoding($false)))
  try {
    az containerapp job create -n $job -g $RG --yaml $yamlPath | Out-Null
    Assert-LastExit "[$Name] job create failed"
  }
  finally {
    Remove-Item $yamlPath -ErrorAction SilentlyContinue
  }

  Write-Step "[$Name] starting the Job..."
  $exec = az containerapp job start -n $job -g $RG --query name -o tsv
  Assert-LastExit "[$Name] job start failed"

  Write-Step "[$Name] waiting for execution '$exec'..."
  $status = ''
  for ($i = 0; $i -lt 120; $i++) {
    # execution list + name filter avoids the `show` flag whose name varies across az versions.
    $status = az containerapp job execution list -n $job -g $RG `
      --query "[?name=='$exec'].properties.status | [0]" -o tsv 2>$null
    if ($status -in @('Succeeded', 'Failed')) { break }
    Start-Sleep -Seconds 5
  }

  if ($status -eq 'Succeeded') {
    Write-Ok "[$Name] principal registered + granted (execution Succeeded)"
    az containerapp job delete -n $job -g $RG --yes 2>$null | Out-Null
  }
  else {
    $shownStatus = if ($status) { $status } else { 'unknown' }
    Write-Host "[X] [$Name] execution status: $shownStatus - fetching logs..." -ForegroundColor Red
    az containerapp job logs show -n $job -g $RG --container pgboot --execution $exec --tail 100 2>$null
    Write-Host "  Job '$job' left in place for inspection; delete with: az containerapp job delete -n $job -g $RG --yes"
    Die "[$Name] bootstrap failed"
  }
}

foreach ($t in $Targets) { Invoke-BootstrapOne $t }

Write-Ok "Bootstrap complete for: $($Targets -join ', '). Verify with quickstart Scenario 1 (apps connect with no password)."
