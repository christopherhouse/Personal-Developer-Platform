CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'ipam') THEN
            CREATE SCHEMA ipam;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    CREATE EXTENSION IF NOT EXISTS btree_gist SCHEMA public;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    CREATE TABLE ipam.region_pool (
        id uuid NOT NULL,
        region text NOT NULL,
        region_index smallint NOT NULL,
        supernet cidr NOT NULL,
        hub_carveout cidr,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_region_pool PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    CREATE TABLE ipam.allocation (
        id uuid NOT NULL,
        pool_id uuid NOT NULL,
        name text NOT NULL,
        network cidr NOT NULL,
        prefix_length smallint NOT NULL,
        kind text NOT NULL,
        allocated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_allocation PRIMARY KEY (id),
        CONSTRAINT fk_allocation_region_pool_pool_id FOREIGN KEY (pool_id) REFERENCES ipam.region_pool (id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    CREATE UNIQUE INDEX uq_allocation_pool_name ON ipam.allocation (pool_id, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    CREATE UNIQUE INDEX ix_region_pool_region ON ipam.region_pool (region);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    CREATE UNIQUE INDEX ix_region_pool_region_index ON ipam.region_pool (region_index);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    ALTER TABLE ipam.allocation ADD CONSTRAINT allocations_no_overlap EXCLUDE USING gist (pool_id WITH =, network inet_ops WITH &&);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    ALTER TABLE ipam.region_pool ADD CONSTRAINT region_pool_supernet_no_overlap EXCLUDE USING gist (supernet inet_ops WITH &&);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    INSERT INTO ipam.region_pool (id, region, region_index, supernet, hub_carveout, created_at)
    VALUES ('11111111-1111-1111-1111-111111111111', 'platform', 0, '10.0.0.0/16', NULL, '2026-01-01T00:00:00+00:00')
    ON CONFLICT (id) DO NOTHING;

    INSERT INTO ipam.allocation (id, pool_id, name, network, prefix_length, kind, allocated_at)
    VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111', 'control-plane-vnet',
            '10.0.0.0/24', 24, 'reservation', '2026-01-01T00:00:00+00:00')
    ON CONFLICT (id) DO NOTHING;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260622150020_InitialIpamSchema') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260622150020_InitialIpamSchema', '10.0.9');
    END IF;
END $EF$;
COMMIT;

