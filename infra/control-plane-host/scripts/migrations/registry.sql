CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    migration_id character varying(150) NOT NULL,
    product_version character varying(32) NOT NULL,
    CONSTRAINT pk___ef_migrations_history PRIMARY KEY (migration_id)
);

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
        IF NOT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname = 'registry') THEN
            CREATE SCHEMA registry;
        END IF;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    CREATE TABLE registry.environment_saga (
        id uuid NOT NULL,
        status text NOT NULL,
        current_phase text,
        pending_confirmation boolean NOT NULL,
        current_run_id uuid,
        version integer NOT NULL,
        CONSTRAINT pk_environment_saga PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    CREATE TABLE registry.environments (
        env_id uuid NOT NULL,
        kind text NOT NULL,
        subscription text NOT NULL,
        region text NOT NULL,
        name text NOT NULL,
        owner text NOT NULL,
        status text NOT NULL,
        spoke_cidr cidr,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_environments PRIMARY KEY (env_id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    CREATE TABLE registry.provisioning_runs (
        run_id uuid NOT NULL,
        env_id uuid NOT NULL,
        phase text NOT NULL,
        workflow_file text NOT NULL,
        dispatch_inputs jsonb NOT NULL,
        github_run_id bigint,
        github_run_url text,
        outcome text NOT NULL,
        plan_summary text,
        dispatched_at timestamp with time zone,
        completed_at timestamp with time zone,
        tracked_by text,
        CONSTRAINT pk_provisioning_runs PRIMARY KEY (run_id),
        CONSTRAINT fk_provisioning_runs_environments_env_id FOREIGN KEY (env_id) REFERENCES registry.environments (env_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    CREATE UNIQUE INDEX uq_environment_natural_key ON registry.environments (kind, subscription, name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    CREATE INDEX ix_provisioning_runs_env_id ON registry.provisioning_runs (env_id);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    CREATE UNIQUE INDEX uq_provisioning_run_github_run ON registry.provisioning_runs (env_id, github_run_id) WHERE github_run_id IS NOT NULL;
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260617204456_InitialRegistrySchema') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260617204456_InitialRegistrySchema', '10.0.9');
    END IF;
END $EF$;
COMMIT;

START TRANSACTION;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    CREATE TABLE registry.archetypes (
        name text NOT NULL,
        description text NOT NULL,
        status text NOT NULL,
        created_at timestamp with time zone NOT NULL,
        updated_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_archetypes PRIMARY KEY (name)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    CREATE TABLE registry.catalog_syncs (
        id uuid NOT NULL,
        content_hash text NOT NULL,
        outcome text NOT NULL,
        summary jsonb NOT NULL,
        applied_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_catalog_syncs PRIMARY KEY (id)
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    CREATE TABLE registry.archetype_versions (
        archetype_name text NOT NULL,
        version text NOT NULL,
        module_path text NOT NULL,
        parameter_schema jsonb NOT NULL,
        content_hash text NOT NULL,
        registered_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_archetype_versions PRIMARY KEY (archetype_name, version),
        CONSTRAINT fk_archetype_versions_archetypes_archetype_name FOREIGN KEY (archetype_name) REFERENCES registry.archetypes (name) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    CREATE TABLE registry.workloads (
        env_id uuid NOT NULL,
        spoke_subscription text NOT NULL,
        spoke_name text NOT NULL,
        archetype_name text NOT NULL,
        archetype_version text NOT NULL,
        pdp_env text NOT NULL,
        parameters jsonb NOT NULL,
        created_at timestamp with time zone NOT NULL,
        CONSTRAINT pk_workloads PRIMARY KEY (env_id),
        CONSTRAINT fk_workloads_archetype_versions_archetype_name_archetype_versi FOREIGN KEY (archetype_name, archetype_version) REFERENCES registry.archetype_versions (archetype_name, version) ON DELETE RESTRICT,
        CONSTRAINT fk_workloads_environments_env_id FOREIGN KEY (env_id) REFERENCES registry.environments (env_id) ON DELETE CASCADE
    );
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    CREATE INDEX ix_workloads_archetype_name_archetype_version ON registry.workloads (archetype_name, archetype_version);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    CREATE INDEX ix_workloads_spoke ON registry.workloads (spoke_subscription, spoke_name);
    END IF;
END $EF$;

DO $EF$
BEGIN
    IF NOT EXISTS(SELECT 1 FROM "__EFMigrationsHistory" WHERE "migration_id" = '20260702185823_WorkloadCatalog') THEN
    INSERT INTO "__EFMigrationsHistory" (migration_id, product_version)
    VALUES ('20260702185823_WorkloadCatalog', '10.0.9');
    END IF;
END $EF$;
COMMIT;

