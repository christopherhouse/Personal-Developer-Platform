namespace Pdp.Mcp.Confirm;

/// <summary>
/// The gated operation a confirmation token authorizes (data-model §5). Bound into the token at issue and
/// re-checked at apply/destroy so a token minted for one operation can never release another.
/// </summary>
public enum ConfirmationOperation
{
    /// <summary>Vend a spoke (the apply that follows <c>PlanSpokeVend</c>).</summary>
    SpokeVend,

    /// <summary>Destroy a spoke (the destroy that follows <c>PlanSpokeDestroy</c>).</summary>
    SpokeDestroy,

    /// <summary>Create a regional fabric (the apply that follows <c>PlanFabricCreate</c>).</summary>
    FabricCreate,

    /// <summary>Destroy a regional fabric (the destroy that follows <c>PlanFabricDestroy</c>).</summary>
    FabricDestroy,
}

/// <summary>
/// The MCP host's plan→confirm token store (contracts/mcp-tool-surface.md §plan/confirm; data-model §5).
/// Realizes Article VIII for the conversational surface: a <c>Plan*</c> tool <see cref="Issue"/>s an
/// opaque, single-use, ~5-minute token bound to <c>{operation, targetName}</c>; the matching
/// <c>Apply*</c>/<c>Destroy*</c> tool must <see cref="Validate"/> it with the <b>verbatim</b> target before
/// any verb runs. The model cannot fabricate a valid token, so a chat turn can never mutate without an
/// explicit, target-restating confirmation (FR-012/FR-013, SC-002).
/// </summary>
public interface IConfirmationTokens
{
    /// <summary>
    /// Issues a fresh single-use token for <paramref name="operation"/> on <paramref name="targetName"/>,
    /// expiring ~5 minutes from now. The returned value is opaque and unguessable.
    /// </summary>
    string Issue(ConfirmationOperation operation, string targetName);

    /// <summary>
    /// Consumes <paramref name="token"/>, throwing <see cref="ModelContextProtocol.McpException"/> unless it
    /// is present, unexpired, matches <paramref name="operation"/>, and restates <paramref name="targetName"/>
    /// verbatim. A successful validation removes the token (single-use); an expired token is also removed.
    /// A mismatch leaves the token intact so an accidental typo does not burn a valid confirmation.
    /// </summary>
    void Validate(string token, ConfirmationOperation operation, string targetName);
}
