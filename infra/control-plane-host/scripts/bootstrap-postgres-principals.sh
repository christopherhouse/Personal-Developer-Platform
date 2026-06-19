#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# bootstrap-postgres-principals.sh — the SC-010 "single reviewed manual step", automated as a
# transient in-VNet ACA Job. Registers the per-app UAMIs (uami-api / uami-mcp) as Postgres Entra
# principals + grants them on ipam/registry, so the apps' token logins succeed (spec 007 research §6).
#
# WHY A JOB: the ledger Postgres is private (VNet-injected) + Entra-only, and pgaadauth_* must run as the
# Entra ADMIN. This stack's ACA managed environment is already in that VNet, so we run psql there — as a
# short-lived Manual ACA Job — while YOU stay on your laptop/Cloud Shell. `az` is a control-plane call;
# the psql happens inside the Job, in-VNet. Your oss-rdbms token (minted as the Entra admin) rides in as a
# Job SECRET and is the connection "password". The Job is deleted as soon as it finishes.
#
# This is still a SINGLE REVIEWED MANUAL STEP (Article I / SC-010): a human runs it, reviews the SQL it
# emits, and it touches the ledger exactly once. It is IDEMPOTENT (pgaadauth_create_principal_with_oid +
# GRANTs are safe to re-run).
#
# PREREQUISITES
#   - The host stack has been applied (the UAMIs + ACA env exist) and images are pushed.
#   - You are signed in as the Postgres Entra ADMIN:  az login
#   - Tools: az (with the containerapp extension), tofu, base64. Run from anywhere with internet.
#   - Run from the stack dir so `tofu output` resolves:  cd infra/control-plane-host
#
# USAGE
#   ./scripts/bootstrap-postgres-principals.sh            # bootstrap both api and mcp
#   ./scripts/bootstrap-postgres-principals.sh api        # just one
#   PG_BOOT_IMAGE=myacr.azurecr.io/postgres:16 ./scripts/bootstrap-postgres-principals.sh   # mirror image
# ---------------------------------------------------------------------------
set -euo pipefail

# psql image. Public Docker Hub by default; override PG_BOOT_IMAGE if Docker Hub pulls are blocked in your
# environment (e.g. point at a mirrored copy in this stack's ACR).
PG_BOOT_IMAGE="${PG_BOOT_IMAGE:-postgres:16-alpine}"

# Which principals to bootstrap (positional args; default both). Each name maps to a tofu output.
targets=("${@:-api mcp}")
# shellcheck disable=SC2206
targets=(${targets[@]})

log()  { printf '\033[1;36m▶ %s\033[0m\n' "$*" >&2; }
ok()   { printf '\033[1;32m✓ %s\033[0m\n' "$*" >&2; }
die()  { printf '\033[1;31m✗ %s\033[0m\n' "$*" >&2; exit 1; }

command -v az    >/dev/null || die "az CLI not found"
command -v tofu  >/dev/null || die "tofu not found (run from infra/control-plane-host)"
command -v base64 >/dev/null || die "base64 not found"

log "Reading stack coordinates from tofu output…"
ctx_json="$(tofu output -json bootstrap_context 2>/dev/null || true)"
if [ -z "$ctx_json" ]; then
  # The local dir may not be init'd against the real backend (e.g. only -backend=false for validation).
  log "tofu backend not initialized here — running 'tofu init -reconfigure' (read-only)…"
  tofu init -reconfigure -input=false >/dev/null || die "tofu init failed — check: run from infra/control-plane-host, az login is valid, and you can reach the state storage account (overnight policy may have disabled its public network access)"
  ctx_json="$(tofu output -json bootstrap_context 2>/dev/null || true)"
fi
[ -n "$ctx_json" ] || die "no 'bootstrap_context' output — the host stack is NOT applied yet. This is a POST-deploy step: deploy infra/control-plane-host first (CI apply-on-merge / iac-apply), THEN re-run this."
jqf() { printf '%s' "$ctx_json" | python -c "import sys,json;print(json.load(sys.stdin)['$1'])" 2>/dev/null \
        || printf '%s' "$ctx_json" | sed -n "s/.*\"$1\": *\"\([^\"]*\)\".*/\1/p"; }
RG="$(jqf resource_group)"
ENV_ID="$(jqf aca_environment_id)"
PGHOST="$(jqf ledger_fqdn)"
PGDB="$(jqf postgres_database)"
[ -n "$RG" ] && [ -n "$ENV_ID" ] && [ -n "$PGHOST" ] && [ -n "$PGDB" ] || die "incomplete bootstrap_context (rg=$RG env=$ENV_ID host=$PGHOST db=$PGDB)"

# Region for the Job (matches the env's region; the host stack is single-region westus3).
LOCATION="$(az group show -n "$RG" --query location -o tsv)"

log "Confirming Azure sign-in (must be the Postgres Entra admin)…"
UPN="$(az account show --query user.name -o tsv)" || die "not signed in — run: az login"
ok "Signed in as: $UPN  (this identity MUST be the ledger's Entra admin)"

# Mint the owner's Postgres (oss-rdbms) access token — this is the psql 'password'. ~1h lifetime; plenty.
log "Minting oss-rdbms access token…"
TOKEN="$(az account get-access-token --resource-type oss-rdbms --query accessToken -o tsv)" \
  || die "could not get an oss-rdbms token"

run_one() {
  local name="$1"                      # api | mcp
  local out="pgaadauth_bootstrap_uami_${name}"
  local job="caj-pgboot-${name}"

  log "[$name] fetching the bootstrap SQL from tofu output '$out'…"
  local sql sql_b64
  sql="$(tofu output -raw "$out")" || die "[$name] no output '$out'"
  # base64 (single line, no shell-special chars) sidesteps all YAML/CLI quoting of multi-line SQL.
  sql_b64="$(printf '%s' "$sql" | base64 | tr -d '\n')"

  printf '\n------ SQL that will run for %s (review) ------\n%s\n------------------------------------------------\n\n' "$name" "$sql" >&2

  # Clean any leftover job from a previous run (idempotent).
  az containerapp job delete -n "$job" -g "$RG" --yes >/dev/null 2>&1 || true

  log "[$name] creating transient in-VNet ACA Job '$job'…"
  local yaml; yaml="$(mktemp)"
  cat >"$yaml" <<YAML
location: ${LOCATION}
properties:
  environmentId: ${ENV_ID}
  configuration:
    triggerType: Manual
    replicaTimeout: 600
    replicaRetryLimit: 0
    manualTriggerConfig:
      parallelism: 1
      replicaCompletionCount: 1
    secrets:
      - name: pgtoken
        value: "${TOKEN}"
  template:
    containers:
      - name: pgboot
        image: ${PG_BOOT_IMAGE}
        resources:
          cpu: 0.25
          memory: 0.5Gi
        command: ["/bin/sh", "-c"]
        args:
          - 'echo "\$SQL_B64" | base64 -d | psql -v ON_ERROR_STOP=1 -f -'
        env:
          - name: PGHOST
            value: "${PGHOST}"
          - name: PGPORT
            value: "5432"
          - name: PGDATABASE
            value: "${PGDB}"
          - name: PGUSER
            value: "${UPN}"
          - name: PGSSLMODE
            value: "require"
          - name: PGPASSWORD
            secretRef: pgtoken
          - name: SQL_B64
            value: "${sql_b64}"
YAML
  az containerapp job create -n "$job" -g "$RG" --yaml "$yaml" >/dev/null
  rm -f "$yaml"

  log "[$name] starting the Job…"
  local exec; exec="$(az containerapp job start -n "$job" -g "$RG" --query name -o tsv)"

  log "[$name] waiting for execution '$exec'…"
  local status=""
  for _ in $(seq 1 120); do
    # execution list + name filter avoids the `show` flag whose name varies across az versions.
    status="$(az containerapp job execution list -n "$job" -g "$RG" \
              --query "[?name=='${exec}'].properties.status | [0]" -o tsv 2>/dev/null || true)"
    case "$status" in Succeeded|Failed) break;; esac
    sleep 5
  done

  if [ "$status" = "Succeeded" ]; then
    ok "[$name] principal registered + granted (execution Succeeded)"
    az containerapp job delete -n "$job" -g "$RG" --yes >/dev/null 2>&1 || true
  else
    printf '\033[1;31m✗ [%s] execution status: %s — fetching logs…\033[0m\n' "$name" "${status:-unknown}" >&2
    az containerapp job logs show -n "$job" -g "$RG" --container pgboot --execution "$exec" --tail 100 \
      2>/dev/null || echo "  (could not stream logs; inspect Job '$job' in the portal / Log Analytics)" >&2
    echo "  Job '$job' left in place for inspection; delete with: az containerapp job delete -n $job -g $RG --yes" >&2
    die "[$name] bootstrap failed"
  fi
}

for t in "${targets[@]}"; do
  case "$t" in
    api|mcp) run_one "$t" ;;
    *) die "unknown target '$t' (expected: api, mcp)" ;;
  esac
done

ok "Bootstrap complete for: ${targets[*]}. Verify with quickstart Scenario 1 (apps connect with no password)."
