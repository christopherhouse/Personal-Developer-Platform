namespace Pdp.ControlPlane.Verbs.Model;

/// <summary>
/// The owner's explicit go-ahead for a mutation, surfaced after a plan (Article VIII / FR-006/FR-007).
/// For <b>create</b>, confirmation MAY be implicit (a <c>--yes</c> after the plan is shown). For
/// <b>destroy</b> it is <b>mandatory and unbypassable</b>: the owner must restate the target name, and
/// the verb layer rejects a missing or mismatched confirmation <i>before</i> any dispatch (enforced in
/// T045 / US2).
/// </summary>
public sealed record Confirmation
{
    /// <summary>Whether the owner has confirmed the mutation.</summary>
    public bool IsConfirmed { get; init; }

    /// <summary>
    /// The target the owner restated (e.g. the spoke name for a destroy). Compared against the
    /// resolved environment; a mismatch is rejected. Null for an implicit create confirmation.
    /// </summary>
    public string? RestatedTarget { get; init; }

    /// <summary>No confirmation given — a mutation presented with this MUST NOT dispatch.</summary>
    public static readonly Confirmation None = new();

    /// <summary>An implicit/explicit confirmation to apply a create (no target restatement required).</summary>
    public static Confirmation ForApply() => new() { IsConfirmed = true };

    /// <summary>An explicit confirmation to destroy, restating the target name (FR-007).</summary>
    public static Confirmation ForDestroy(string restatedTarget) =>
        new() { IsConfirmed = true, RestatedTarget = restatedTarget };
}
