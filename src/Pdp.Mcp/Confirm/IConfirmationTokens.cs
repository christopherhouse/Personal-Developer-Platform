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
/// opaque, single-use, ~15-minute token bound to <c>{operation, targetName}</c>; the matching
/// <c>Apply*</c>/<c>Destroy*</c> tool <see cref="Check"/>s it with the <b>verbatim</b> target before any verb
/// runs, then <see cref="Consume"/>s it <b>only</b> once a gated mutation is actually dispatched. The model
/// cannot fabricate a valid token, so a chat turn can never mutate without an explicit, target-restating
/// confirmation (FR-012/FR-013, SC-002).
/// </summary>
/// <remarks>
/// Check and Consume are split (clarify 2026-06-24) so a premature apply — one whose plan has not yet
/// succeeded — does <b>not</b> burn the token: the verb rejects with "plan not ready" between Check and
/// Consume, leaving the token redeemable once the plan finishes. The token is issued at plan <i>dispatch</i>,
/// so its ~15-minute window covers the plan run's queue + execution + the owner's review.
/// </remarks>
public interface IConfirmationTokens
{
    /// <summary>
    /// Issues a fresh single-use token for <paramref name="operation"/> on <paramref name="targetName"/>,
    /// expiring ~15 minutes from now. The returned value is opaque and unguessable.
    /// </summary>
    string Issue(ConfirmationOperation operation, string targetName);

    /// <summary>
    /// Validates <paramref name="token"/> <b>without consuming it</b>, throwing
    /// <see cref="ModelContextProtocol.McpException"/> unless it is present, unexpired, matches
    /// <paramref name="operation"/>, and restates <paramref name="targetName"/> verbatim. An expired token is
    /// removed; a mismatch leaves it intact so an accidental typo does not burn a valid confirmation. Because
    /// it does not remove the token, a rejected gated mutation (e.g. "plan not ready") leaves it redeemable.
    /// </summary>
    void Check(string token, ConfirmationOperation operation, string targetName);

    /// <summary>
    /// Removes <paramref name="token"/> (single-use) — called <b>only after a gated mutation has actually
    /// been dispatched</b>. Idempotent: removing an absent token is a no-op.
    /// </summary>
    void Consume(string token);
}
