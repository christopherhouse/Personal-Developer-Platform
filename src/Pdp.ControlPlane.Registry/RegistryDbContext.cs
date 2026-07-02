using Microsoft.EntityFrameworkCore;
using Pdp.ControlPlane.Registry.Entities;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// The control-plane registry's EF Core context: the <c>registry</c> schema in the existing platform
/// Postgres (beside <c>ipam</c> and Wolverine's <c>wolverine</c> schema). Three tables —
/// <c>environments</c> (intent + lifecycle), <c>provisioning_runs</c> (audit), and
/// <c>environment_saga</c> (Wolverine EF Core saga storage). snake_case identifiers; IP ranges are
/// native <c>cidr</c> (<see cref="System.Net.IPNetwork"/>); enums persist as readable text. The
/// schema is independently droppable for teardown (Article IV / SC-010). Wolverine maps the saga type
/// here so its EF Core saga storage can persist it (research §2).
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

            // Composite covering index for the hot query pattern filter-by-env_id + sort-by-dispatched_at
            // DESC (RunTracker, EnvironmentRegistry.GetRunsAsync). Replaces the single-column index so
            // PostgreSQL can satisfy the filter + sort from the index alone without a sort step (issue #59).
            entity.HasIndex(r => new { r.EnvId, r.DispatchedAt })
                .HasDatabaseName("ix_provisioning_runs_env_id_dispatched")
                .IsDescending(false, true); // env_id ASC, dispatched_at DESC

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
