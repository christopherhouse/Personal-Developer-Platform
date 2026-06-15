#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Idempotently configures the branch-protection ruleset on `main` (FR-014, research §9).

.DESCRIPTION
    Repo configuration, not Azure infrastructure, so it lives in scripts/ rather than
    OpenTofu (a github provider would drag a new provider + state in for one resource).
    Creates or updates a branch ruleset named "pdp-main-protection" that:
      - requires a pull request for every change (direct pushes to main rejected),
      - requires the iac-plan status checks ('fmt', 'plan (foundations)') to pass,
      - blocks force pushes (non_fast_forward) and branch deletion,
      - allows NO bypass actors (applies to the owner/admin too).

    Idempotent: re-running updates the existing ruleset in place (matched by name).
    Requires: gh CLI authenticated with the 'repo' scope and admin on the repository.

.EXAMPLE
    ./scripts/setup-branch-protection.ps1
    ./scripts/setup-branch-protection.ps1 -Repository christopherhouse/Personal-Developer-Platform
#>
[CmdletBinding()]
param(
    # <owner>/<repo>. Defaults to the repo's gh-detected remote, else the platform repo.
    [string]$Repository,

    # Status-check contexts that must pass before merge (job names in iac-plan.yml).
    [string[]]$RequiredChecks = @('fmt', 'plan (foundations)'),

    [string]$RulesetName = 'pdp-main-protection'
)

$ErrorActionPreference = 'Stop'

if (-not $Repository) {
    try { $Repository = (gh repo view --json nameWithOwner --jq '.nameWithOwner' 2>$null) } catch {}
    if (-not $Repository) { $Repository = 'christopherhouse/Personal-Developer-Platform' }
}
Write-Host "Configuring ruleset '$RulesetName' on $Repository (branch: main)..."

# Desired ruleset definition (GitHub repo rulesets API).
$ruleset = [ordered]@{
    name         = $RulesetName
    target       = 'branch'
    enforcement  = 'active'
    bypass_actors = @()   # no bypass — the owner goes through PRs too (FR-014)
    conditions   = @{
        ref_name = @{
            include = @('~DEFAULT_BRANCH')
            exclude = @()
        }
    }
    rules = @(
        @{ type = 'pull_request'; parameters = @{
                required_approving_review_count   = 0   # single-owner platform: PR required, self-merge after checks
                dismiss_stale_reviews_on_push     = $false
                require_code_owner_review         = $false
                require_last_push_approval        = $false
                required_review_thread_resolution = $false
            }
        },
        @{ type = 'required_status_checks'; parameters = @{
                strict_required_status_checks_policy = $true
                required_status_checks = @($RequiredChecks | ForEach-Object { @{ context = $_ } })
            }
        },
        @{ type = 'non_fast_forward' },   # block force pushes
        @{ type = 'deletion' }            # block branch deletion
    )
}

$payload = $ruleset | ConvertTo-Json -Depth 10

# Find an existing ruleset by name (idempotency).
$existing = gh api "repos/$Repository/rulesets" --jq "map(select(.name == \`"$RulesetName\`")) | .[0].id" 2>$null

if ($existing) {
    Write-Host "Updating existing ruleset (id: $existing)..."
    $payload | gh api --method PUT "repos/$Repository/rulesets/$existing" --input - | Out-Null
} else {
    Write-Host "Creating ruleset..."
    $payload | gh api --method POST "repos/$Repository/rulesets" --input - | Out-Null
}

Write-Host "Done. Ruleset '$RulesetName' is active on main."
Write-Host "Verify: gh api repos/$Repository/rulesets --jq '.[].name'"
