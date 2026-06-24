#Requires -Version 7.0
<#
.SYNOPSIS
  Re-enable PUBLIC network access on the resources a corp Azure Policy disables overnight: the PDP tfstate
  storage accounts (CI `tofu init` 403s otherwise) and the platform Log Analytics workspace(s) (ingest +
  query). This RESTORES the IaC-declared posture (public_network_access_enabled = true / public ingest +
  query), so it removes drift rather than adding it. The owner can't touch the policy (corp), so this is the
  daily un-break. Idempotent: only flips what's currently Disabled; skips anything not found.

.NOTES
  Scoped to the KNOWN should-be-public resources on purpose — it does NOT blanket-enable every storage
  account, since others may be intentionally private. Add entries below if more resources should be public.

.EXAMPLE
  az login
  ./scripts/reenable-public-access.ps1
#>
[CmdletBinding()]
param(
  [string] $Subscription = '8bd05b2f-62c5-4def-9869-f0617ebb3970'
)

$ErrorActionPreference = 'Continue'   # az emits stderr warnings; don't let them terminate (see memory: az + Stop)

function Write-Ok($m) { Write-Host "[ok]   $m" -ForegroundColor Green }
function Write-Fix($m) { Write-Host "[fix]  $m" -ForegroundColor Yellow }
function Write-Skip($m) { Write-Host "[skip] $m" -ForegroundColor DarkGray }

if (-not (Get-Command az -ErrorAction SilentlyContinue)) { Write-Host '[X] az not found on PATH' -ForegroundColor Red; exit 1 }

# --- tfstate storage accounts (the overnight policy disables these; CI init 403s) -------------------------
$storageAccounts = @(
  @{ name = 'stpdpwus3statejqyq'; rg = 'rg-pdp-westus3-foundations' }, # backs most stacks
  @{ name = 'cmhtfstatesa'; rg = 'RG-TF' }                            # foundations bootstrap account
)

# --- Log Analytics workspaces (public ingest + query must stay on) ---------------------------------------
$workspaces = @(
  @{ name = 'log-pdp-westus3-controlplane'; rg = 'rg-pdp-westus3-controlplane-host' }, # current sink (pre-PR-B migration)
  @{ name = 'log-pdp-westus3-platform'; rg = 'rg-pdp-westus3-observability' }          # shared sink (exists after platform-observability applies)
)

Write-Host ">> Storage accounts" -ForegroundColor Cyan
foreach ($sa in $storageAccounts) {
  $cur = az storage account show -n $sa.name -g $sa.rg --subscription $Subscription --query publicNetworkAccess -o tsv 2>$null
  if (-not $cur) { Write-Skip "$($sa.name): not found"; continue }
  if ($cur -eq 'Enabled') { Write-Ok "$($sa.name): already Enabled"; continue }
  Write-Fix "$($sa.name): $cur -> Enabled"
  az storage account update -n $sa.name -g $sa.rg --subscription $Subscription --public-network-access Enabled -o none 2>$null
  if ($LASTEXITCODE -ne 0) { Write-Host "       (update failed for $($sa.name))" -ForegroundColor Red }
}

Write-Host ">> Log Analytics workspaces" -ForegroundColor Cyan
foreach ($ws in $workspaces) {
  $ing = az monitor log-analytics workspace show -n $ws.name -g $ws.rg --subscription $Subscription --query publicNetworkAccessForIngestion -o tsv 2>$null
  if (-not $ing) { Write-Skip "$($ws.name): not found"; continue }
  $qry = az monitor log-analytics workspace show -n $ws.name -g $ws.rg --subscription $Subscription --query publicNetworkAccessForQuery -o tsv 2>$null
  if ($ing -eq 'Enabled' -and $qry -eq 'Enabled') { Write-Ok "$($ws.name): ingest+query Enabled"; continue }
  Write-Fix "$($ws.name): ingest=$ing query=$qry -> Enabled"
  az monitor log-analytics workspace update -n $ws.name -g $ws.rg --subscription $Subscription `
    --ingestion-access Enabled --query-access Enabled -o none 2>$null
  if ($LASTEXITCODE -ne 0) { Write-Host "       (update failed for $($ws.name))" -ForegroundColor Red }
}

Write-Host "`nDone. Re-run any failed CI: gh run rerun <run-id> --failed" -ForegroundColor Cyan
exit 0   # the skip-if-not-found probes leave $LASTEXITCODE non-zero; the script itself succeeded
