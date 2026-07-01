-- DESTRUCTIVE. Clean-slate reset of the control-plane ledger (ipam) + registry (environments/runs).
-- Applied by reset-ledger.ps1 via a transient in-VNet ACA Job (same mechanism as run-migrations.ps1).
--
-- Keeps: the seeded baseline (platform supernet pool + the control-plane-vnet reservation) and every
-- region pool that still has a real fabric in Azure — EXCEPT this script hard-drops the phantom
-- swedencentral pool (region_index 3). Before running, confirm which region pools correspond to a live
-- fabric (whats_deployed) and adjust the region_index filter below if your topology differs.
-- Removes: all spoke allocations and the ENTIRE registry env/deployment/run history.
--
-- IMPORTANT: destroy any real spokes in AZURE first (spoke-destroy.yml) — deleting their ledger rows
-- while their resources still exist creates the worse drift (live Azure with no ledger record + a
-- re-allocatable CIDR). Runs in one transaction; ON_ERROR_STOP=1 aborts everything on any error.

BEGIN;

-- IPAM: remove every allocation EXCEPT the seeded control-plane-vnet reservation (deterministic id).
-- Targeting by id avoids any enum-casing ambiguity on the `kind` column.
DELETE FROM ipam.allocation
 WHERE id <> '22222222-2222-2222-2222-222222222222';

-- IPAM: drop the phantom swedencentral region pool (region_index 3) — no fabric exists for it in Azure.
-- (ON DELETE CASCADE would clear its allocations; it has none.) platform (0) + westus3 (2) are kept.
DELETE FROM ipam.region_pool
 WHERE region_index = 3;

-- Registry: wipe all environment + deployment/run history for a clean slate.
-- CASCADE covers the provisioning_runs -> environments FK.
TRUNCATE registry.provisioning_runs, registry.environment_saga, registry.environments
  RESTART IDENTITY CASCADE;

COMMIT;

-- Report the resulting baseline.
\echo '--- region_pool (expect platform/0 + westus3/2) ---'
SELECT region, region_index, supernet, hub_carveout FROM ipam.region_pool ORDER BY region_index;
\echo '--- allocation (expect only control-plane-vnet reservation) ---'
SELECT name, network, kind FROM ipam.allocation ORDER BY name;
\echo '--- registry counts (expect all zero) ---'
SELECT 'environments' AS tbl, count(*) FROM registry.environments
UNION ALL SELECT 'provisioning_runs', count(*) FROM registry.provisioning_runs
UNION ALL SELECT 'environment_saga', count(*) FROM registry.environment_saga;
