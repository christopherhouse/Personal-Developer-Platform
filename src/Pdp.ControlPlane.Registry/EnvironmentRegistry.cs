using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pdp.ControlPlane.Registry.Entities;
using Environment = Pdp.ControlPlane.Registry.Entities.Environment;

namespace Pdp.ControlPlane.Registry;

/// <summary>
/// The intent registry over <see cref="RegistryDbContext"/> (data-model §1). The natural-key unique
/// index (<c>uq_environment_natural_key</c>) is the convergence + concurrency backstop: a race on the
/// same <c>(kind, subscription, name)</c> surfaces as a unique-violation that this type maps to the
/// single-flight rejection (FR-022a), so two callers never split one environment into two surrogates.
/// </summary>
public sealed class EnvironmentRegistry(RegistryDbContext context) : IEnvironmentRegistry
{
    /// <summary>The non-terminal statuses that block a new mutation (the single-flight set).</summary>
    private static bool IsNonTerminal(EnvironmentStatus status) => status is
        EnvironmentStatus.Requested or
        EnvironmentStatus.Provisioning or
        EnvironmentStatus.Destroying;

    /// <inheritdoc />
    public async Task<Guid> BeginCreateAsync(
        EnvironmentKind kind,
        string subscription,
        string region,
        string name,
        string owner,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;

        var existing = await context.Environments
            .SingleOrDefaultAsync(
                e => e.Kind == kind && e.Subscription == subscription && e.Name == name,
                cancellationToken);

        if (existing is not null)
        {
            if (IsNonTerminal(existing.Status))
            {
                throw new OperationInProgressException(existing.EnvId, existing.Status);
            }

            // Convergent re-create of a terminal environment (FR-022): same surrogate, reset to Requested.
            existing.Status = EnvironmentStatus.Requested;
            existing.Region = region;
            existing.Owner = owner;
            existing.UpdatedAt = now;
            await context.SaveChangesAsync(cancellationToken);
            return existing.EnvId;
        }

        var environment = new Environment
        {
            EnvId = Guid.CreateVersion7(),
            Kind = kind,
            Subscription = subscription,
            Region = region,
            Name = name,
            Owner = owner,
            Status = EnvironmentStatus.Requested,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.Environments.Add(environment);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
        })
        {
            // A concurrent caller claimed the same natural key first — treat as single-flight (FR-022a).
            context.Entry(environment).State = EntityState.Detached;
            var winner = await context.Environments
                .AsNoTracking()
                .SingleAsync(
                    e => e.Kind == kind && e.Subscription == subscription && e.Name == name,
                    cancellationToken);
            throw new OperationInProgressException(winner.EnvId, winner.Status);
        }

        return environment.EnvId;
    }

    /// <inheritdoc />
    public async Task AbortCreateAsync(Guid envId, CancellationToken cancellationToken = default)
    {
        var environment = await context.Environments
            .Include(e => e.Runs)
            .SingleOrDefaultAsync(e => e.EnvId == envId, cancellationToken);
        if (environment is null)
        {
            return;
        }

        if (environment.Runs.Count == 0)
        {
            // Never dispatched anything — remove the claim cleanly so no orphan row remains (FR-023).
            context.Environments.Remove(environment);
        }
        else
        {
            // Has prior history; park it at a safe terminal status rather than deleting audited runs.
            environment.Status = EnvironmentStatus.Failed;
            environment.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task TransitionAsync(
        Guid envId,
        EnvironmentStatus status,
        CancellationToken cancellationToken = default)
    {
        var environment = await context.Environments
            .SingleOrDefaultAsync(e => e.EnvId == envId, cancellationToken)
            ?? throw new InvalidOperationException($"Environment {envId} not found.");

        environment.Status = status;
        environment.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<Environment?> FindByIdAsync(Guid envId, CancellationToken cancellationToken = default) =>
        context.Environments
            .AsNoTracking()
            .SingleOrDefaultAsync(e => e.EnvId == envId, cancellationToken);

    /// <inheritdoc />
    public Task<Environment?> FindByNaturalKeyAsync(
        EnvironmentKind kind,
        string subscription,
        string name,
        CancellationToken cancellationToken = default) =>
        context.Environments
            .AsNoTracking()
            .SingleOrDefaultAsync(
                e => e.Kind == kind && e.Subscription == subscription && e.Name == name,
                cancellationToken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<ProvisioningRun>> GetRunsAsync(
        Guid envId,
        CancellationToken cancellationToken = default) =>
        await context.ProvisioningRuns
            .AsNoTracking()
            .Where(r => r.EnvId == envId)
            .OrderByDescending(r => r.DispatchedAt)
            .ToListAsync(cancellationToken);

    /// <inheritdoc />
    public Task<ProvisioningRun?> FindRunByIdAsync(Guid runId, CancellationToken cancellationToken = default) =>
        context.ProvisioningRuns
            .AsNoTracking()
            .SingleOrDefaultAsync(r => r.RunId == runId, cancellationToken);
}
