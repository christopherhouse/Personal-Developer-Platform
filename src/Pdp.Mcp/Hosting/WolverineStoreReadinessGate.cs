using Npgsql;

namespace Pdp.Mcp.Hosting;

/// <summary>
/// Tunables for <see cref="WolverineStoreReadinessGate"/>: how long to wait for the Wolverine message
/// store to appear, and how often to re-probe. Defaults cover the normal cold-start race (the always-on
/// Api node provisions the store within seconds of its own boot); the steady state never waits at all.
/// </summary>
public sealed class WolverineStoreReadinessOptions
{
    /// <summary>Maximum time to wait for the store before failing startup. Default 3 minutes.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>Delay between readiness probes while the store is still absent. Default 3 seconds.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(3);
}

/// <summary>
/// Reports whether the Wolverine message store has been provisioned. Abstracted so the gate's
/// wait/retry/timeout logic is unit-testable without a live Postgres.
/// </summary>
public interface IMessageStoreProbe
{
    /// <summary>Returns <see langword="true"/> once the <c>wolverine</c> message store exists.</summary>
    Task<bool> ExistsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// The live probe: a single cheap catalog lookup over the shared Entra-token data source. Presence of
/// <c>wolverine.wolverine_nodes</c> — the table Wolverine's Solo-mode node controller reads first on
/// startup — means the Api node has provisioned the store.
/// </summary>
public sealed class PostgresMessageStoreProbe(NpgsqlDataSource dataSource) : IMessageStoreProbe
{
    // to_regclass returns NULL (not an error) for a missing relation, so this never throws on "not yet
    // there" — only on a genuine connection failure, which the gate treats as not-ready-keep-waiting.
    private const string ProbeSql = "SELECT to_regclass('wolverine.wolverine_nodes') IS NOT NULL";

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(ProbeSql);
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is true;
    }
}

/// <summary>
/// A startup gate that holds the scale-to-zero MCP node back until the shared <c>wolverine</c> message
/// store exists. The MCP node, by design, NEVER provisions that store: it sets
/// <c>AutoBuildMessageStorageOnStartup = AutoCreate.None</c> (it runs as <c>uami-mcp</c>, which does not
/// own the schema) and relies on the always-on Api node to build it
/// (<c>WolverineConfiguration.ConfigureCommon</c>). But Wolverine's Solo-mode node controller still reads
/// <c>wolverine.wolverine_nodes</c> on startup — so if the MCP node cold-starts before the Api has built
/// the schema, that read throws <c>42P01</c> as an UNHANDLED exception, the process exits, and ACA marks
/// the revision <c>ActivationFailed</c> permanently — never retrying even once the schema appears (the
/// 2026-06-24 telemetry-outage incident).
///
/// <para>
/// Registered immediately BEFORE <c>UseWolverine</c> so it runs first (hosted services start in
/// registration order); the web host and its unauthenticated <c>/healthz</c> endpoint are registered
/// earlier still, so the ACA startup probe can succeed while this gate waits. The gate polls for the
/// store and returns the moment it exists — converting a permanent crash into a bounded, self-healing
/// wait. In steady state the table already exists, so the first probe returns immediately and startup is
/// not delayed.
/// </para>
/// </summary>
public sealed class WolverineStoreReadinessGate(
    IMessageStoreProbe probe,
    WolverineStoreReadinessOptions options,
    TimeProvider timeProvider,
    ILogger<WolverineStoreReadinessGate> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetTimestamp();
        var attempt = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            attempt++;

            bool exists;
            try
            {
                exists = await probe.ExistsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The store not being reachable yet (DB warming up, principal not yet granted) is part of
                // the same race — treat it as "not ready" and keep waiting rather than crashing.
                logger.LogDebug(ex, "Wolverine message-store readiness probe errored; treating as not-ready.");
                exists = false;
            }

            if (exists)
            {
                if (attempt > 1)
                {
                    logger.LogInformation(
                        "Wolverine message store is ready after {Attempts} probe(s) ({Elapsed:0.#}s).",
                        attempt, timeProvider.GetElapsedTime(startedAt).TotalSeconds);
                }

                return;
            }

            var elapsed = timeProvider.GetElapsedTime(startedAt);
            if (elapsed >= options.Timeout)
            {
                throw new TimeoutException(
                    $"The Wolverine message store (wolverine.wolverine_nodes) was not provisioned within " +
                    $"{options.Timeout.TotalSeconds:0}s. The always-on Api node owns and provisions it — " +
                    $"verify the Api container app is healthy and that uami-api holds CREATE on the database.");
            }

            logger.LogInformation(
                "Waiting for the Api node to provision the Wolverine message store " +
                "(probe {Attempt}, {Elapsed:0.#}s elapsed, timeout {Timeout:0}s)...",
                attempt, elapsed.TotalSeconds, options.Timeout.TotalSeconds);

            await Task.Delay(options.PollInterval, timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
