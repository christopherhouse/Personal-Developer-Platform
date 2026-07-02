using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// The control-plane registry's EF Core context: the <c>registry</c> schema in the existing platform
/// Postgres (beside <c>ipam</c> and Wolverine's <c>wolverine</c> schema). Tables —
/// <c>environments</c> (intent + lifecycle), <c>provisioning_runs</c> (audit),
/// <c>environment_saga</c> (Wolverine EF Core saga storage), and the spec-008 catalog projection +
/// workload detail: <c>archetypes</c>, <c>archetype_versions</c>, <c>catalog_syncs</c>,
/// <c>workloads</c>. snake_case identifiers; IP ranges are native <c>cidr</c>
/// (<see cref="System.Net.IPNetwork"/>); enums persist as readable text. The schema is independently
/// droppable for teardown (Article IV / SC-010). Wolverine maps the saga type here so its EF Core
/// saga storage can persist it (research §2).
/// </summary>
public class RegistryDbContext(DbContextOptions<RegistryDbContext> options) : DbContext(options)
{
    /// <summary>The PostgreSQL schema this context owns.</summary>
    public const string Schema = "registry";

    /// <summary>Recorded environment intent + lifecycle (FR-014).</summary>
    public DbSet<Entities.Environment> Environments => Set<Entities.Environment>();

    /// <summary>The provisioning-run audit trail (FR-015).</summary>
    public DbSet<ProvisioningRun> ProvisioningRuns => Set<ProvisioningRun>();

    /// <summary>Wolverine saga state for the environment lifecycle (data-model §3).</summary>
    public DbSet<EnvironmentSaga> EnvironmentSagas => Set<EnvironmentSaga>();

    /// <summary>The catalog projection: archetypes (spec 008; synced from <c>archetypes/catalog.json</c>).</summary>
    public DbSet<Archetype> Archetypes => Set<Archetype>();

    /// <summary>The catalog projection: immutable archetype versions (spec 008, R2).</summary>
    public DbSet<ArchetypeVersion> ArchetypeVersions => Set<ArchetypeVersion>();

    /// <summary>The catalog-sync audit trail (spec 008, FR-006).</summary>
    public DbSet<CatalogSync> CatalogSyncs => Set<CatalogSync>();

    /// <summary>Workload detail rows, 1:1 with <c>kind='workload'</c> environments (spec 008).</summary>
    public DbSet<WorkloadDetails> Workloads => Set<WorkloadDetails>();

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        // Centralized so every consumer (tests, design-time, the host) gets snake_case without
        // having to opt in at registration — mirrors IpamDbContext.
        => optionsBuilder.UseSnakeCaseNamingConvention();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Entities.Environment>(entity =>
        {
            entity.ToTable("environments");
            entity.HasKey(e => e.EnvId);

            // The natural key behind idempotent convergence (FR-022): a re-create of the same
            // (kind, subscription, name) resolves to the existing env_id, never a new surrogate.
            entity.HasIndex(e => new { e.Kind, e.Subscription, e.Name })
                .IsUnique()
                .HasDatabaseName("uq_environment_natural_key");

            entity.Property(e => e.SpokeCidr).HasColumnType("cidr");

            // Persist enums as readable lowercase text so a row's intent is self-evident in the DB
            // (matches the IpamDbContext convention).
            entity.Property(e => e.Kind)
                .HasConversion(k => k.ToString().ToLowerInvariant(),
                    s => Enum.Parse<EnvironmentKind>(s, ignoreCase: true));
            entity.Property(e => e.Status)
                .HasConversion(s => s.ToString().ToLowerInvariant(),
                    s => Enum.Parse<EnvironmentStatus>(s, ignoreCase: true));

            entity.HasMany(e => e.Runs)
                .WithOne(r => r.Environment!)
                .HasForeignKey(r => r.EnvId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ProvisioningRun>(entity =>
        {
            entity.ToTable("provisioning_runs");
            entity.HasKey(r => r.RunId);

            entity.HasIndex(r => r.EnvId).HasDatabaseName("ix_provisioning_runs_env_id");

            // Clean column names for the GitHub fields (snake_case would otherwise split the
            // initialism into git_hub_*); the partial-index filter below relies on these names.
            entity.Property(r => r.GitHubRunId).HasColumnName("github_run_id");
            entity.Property(r => r.GitHubRunUrl).HasColumnName("github_run_url");

            // Dedupe key for idempotent terminal recording (first-terminal-wins; webhook vs
            // reconciler): a given (env_id, github_run_id) records its outcome once (FR-005).
            entity.HasIndex(r => new { r.EnvId, r.GitHubRunId })
                .IsUnique()
                .HasDatabaseName("uq_provisioning_run_github_run")
                .HasFilter("github_run_id IS NOT NULL");

            // The exact dispatched inputs as a queryable jsonb document (data-model §2).
            entity.Property(r => r.DispatchInputs).HasColumnType("jsonb");

            entity.Property(r => r.Phase)
                .HasConversion(p => p.ToString().ToLowerInvariant(),
                    s => Enum.Parse<RunPhase>(s, ignoreCase: true));
            entity.Property(r => r.Outcome)
                .HasConversion(o => o.ToString().ToLowerInvariant(),
                    s => Enum.Parse<RunOutcome>(s, ignoreCase: true));
            entity.Property(r => r.TrackedBy)
                .HasConversion(
                    t => t == null ? null : t.Value.ToString().ToLowerInvariant(),
                    s => s == null ? null : Enum.Parse<TrackingSource>(s, ignoreCase: true));
        });

        modelBuilder.Entity<Archetype>(entity =>
        {
            entity.ToTable("archetypes");
            entity.HasKey(a => a.Name);

            entity.Property(a => a.Status)
                .HasConversion(s => s.ToString().ToLowerInvariant(),
                    s => Enum.Parse<ArchetypeStatus>(s, ignoreCase: true));

            entity.HasMany(a => a.Versions)
                .WithOne(v => v.Archetype!)
                .HasForeignKey(v => v.ArchetypeName)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ArchetypeVersion>(entity =>
        {
            entity.ToTable("archetype_versions");
            // Composite natural key: versions are append-only and content-immutable (R2) — the sync
            // enforces immutability by comparing content_hash for an existing (name, version).
            entity.HasKey(v => new { v.ArchetypeName, v.Version });

            // The version's parameter schema is a queryable JSON document (draft 2020-12).
            entity.Property(v => v.ParameterSchema).HasColumnType("jsonb");
        });

        modelBuilder.Entity<CatalogSync>(entity =>
        {
            entity.ToTable("catalog_syncs");
            entity.HasKey(s => s.Id);

            entity.Property(s => s.Summary).HasColumnType("jsonb");

            // Explicit map (not ToLowerInvariant): NoChange persists as the data-model's `no_change`.
            entity.Property(s => s.Outcome)
                .HasConversion(
                    o => o == CatalogSyncOutcome.NoChange ? "no_change" : o.ToString().ToLowerInvariant(),
                    s => s == "no_change" ? CatalogSyncOutcome.NoChange : Enum.Parse<CatalogSyncOutcome>(s, ignoreCase: true));
        });

        modelBuilder.Entity<WorkloadDetails>(entity =>
        {
            entity.ToTable("workloads");
            entity.HasKey(w => w.EnvId);
            entity.Property(w => w.EnvId).ValueGeneratedNever();

            // 1:1 extension of the managed-unit row: deleting the environment removes the detail.
            entity.HasOne(w => w.Environment)
                .WithOne()
                .HasForeignKey<WorkloadDetails>(w => w.EnvId)
                .OnDelete(DeleteBehavior.Cascade);

            // The stamped version must exist and can never be pulled out from under a workload —
            // Restrict (versions are append-only anyway; this backstops FR-005 at the database).
            entity.HasOne<ArchetypeVersion>()
                .WithMany()
                .HasForeignKey(w => new { w.ArchetypeName, w.ArchetypeVersion })
                .OnDelete(DeleteBehavior.Restrict);

            // Powers the FR-021 spoke-destroy guard ("name the survivors") and spoke-scoped listings.
            entity.HasIndex(w => new { w.SpokeSubscription, w.SpokeName })
                .HasDatabaseName("ix_workloads_spoke");

            entity.Property(w => w.Parameters).HasColumnType("jsonb");
        });

        modelBuilder.Entity<EnvironmentSaga>(entity =>
        {
            // Wolverine EF Core saga storage persists this type; we only supply the normal mapping
            // (research §2). Id == env_id; Version is the optimistic-concurrency token (IRevisioned).
            entity.ToTable("environment_saga");
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Id).ValueGeneratedNever();

            entity.Property(s => s.Status)
                .HasConversion(s => s.ToString().ToLowerInvariant(),
                    s => Enum.Parse<EnvironmentStatus>(s, ignoreCase: true));
            entity.Property(s => s.CurrentPhase)
                .HasConversion(
                    p => p == null ? null : p.Value.ToString().ToLowerInvariant(),
                    s => s == null ? null : Enum.Parse<RunPhase>(s, ignoreCase: true));
        });
    }
}
