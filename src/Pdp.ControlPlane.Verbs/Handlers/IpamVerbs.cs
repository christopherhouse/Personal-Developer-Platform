using Pdp.ControlPlane.Ipam;
using Pdp.ControlPlane.Ipam.Entities;

namespace Pdp.ControlPlane.Verbs.Handlers;

/// <summary>
/// The IPAM verb pass-through to the spec-002 <see cref="IIpamLedger"/> (contracts/verb-surface.md §3).
/// Deliberately a thin adapter: the ledger owns all address logic and the GiST non-overlap backstop, so
/// the verb layer adds no rules — it only exposes the ledger's operation surface through the same typed
/// verb façade the CLI and the future MCP consume.
/// </summary>
public sealed class IpamVerbs(IIpamLedger ledger) : IIpamVerbs
{
    /// <inheritdoc />
    public Task<Allocation> AllocateAsync(string region, string name, int size = 24, CancellationToken cancellationToken = default) =>
        ledger.AllocateAsync(region, name, size, cancellationToken);

    /// <inheritdoc />
    public Task ReleaseAsync(string region, string name, CancellationToken cancellationToken = default) =>
        ledger.ReleaseAsync(region, name, cancellationToken);

    /// <inheritdoc />
    public Task<RegionView> QueryAsync(string region, CancellationToken cancellationToken = default) =>
        ledger.QueryAsync(region, cancellationToken);

    /// <inheritdoc />
    public Task<IReadOnlyList<RegionView>> QueryAllAsync(CancellationToken cancellationToken = default) =>
        ledger.QueryAllAsync(cancellationToken);
}
