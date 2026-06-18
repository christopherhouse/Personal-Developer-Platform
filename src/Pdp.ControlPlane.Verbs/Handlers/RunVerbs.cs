using Pdp.ControlPlane.Registry;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The run / registry-audit verb implementation (FR-015). A thin read over <see cref="IEnvironmentRegistry"/>
/// that projects the EF entities to front-end-agnostic records (<see cref="EnvironmentRecord"/> /
/// <see cref="RunRecord"/>). It records and reads <b>intent and history only</b> — deployed state is
/// never sourced here (FR-016). Unknown references resolve to null, never an error (a clean empty read).
/// </summary>
public sealed class RunVerbs(IEnvironmentRegistry registry) : IRunVerbs
{
    /// <inheritdoc />
    public async Task<EnvironmentRecord?> GetEnvironmentAsync(
        EnvRef environment,
        CancellationToken cancellationToken = default)
    {
        var env = await ResolveAsync(environment, cancellationToken).ConfigureAwait(false);
        return env is null ? null : EnvironmentRecord.From(env);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RunRecord>> GetRunsAsync(
        EnvRef environment,
        CancellationToken cancellationToken = default)
    {
        var env = await ResolveAsync(environment, cancellationToken).ConfigureAwait(false);
        if (env is null)
        {
            return [];
        }

        var runs = await registry.GetRunsAsync(env.EnvId, cancellationToken).ConfigureAwait(false);
        return runs.Select(RunRecord.From).ToList();
    }

    /// <inheritdoc />
    public async Task<RunRecord?> GetRunAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await registry.FindRunByIdAsync(runId, cancellationToken).ConfigureAwait(false);
        return run is null ? null : RunRecord.From(run);
    }

    private async Task<Registry.Entities.Environment?> ResolveAsync(
        EnvRef reference,
        CancellationToken cancellationToken) =>
        reference.EnvId is { } id
            ? await registry.FindByIdAsync(id, cancellationToken).ConfigureAwait(false)
            : reference.HasNaturalKey
                ? await registry
                    .FindByNaturalKeyAsync(reference.Kind!.Value, reference.Subscription!, reference.Name!, cancellationToken)
                    .ConfigureAwait(false)
                : null;
}
