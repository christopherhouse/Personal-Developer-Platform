using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.PlanConfirm;

/// <summary>
/// Enforces the Article VIII confirmation contract at the verb boundary (FR-007). Destroy confirmation
/// is <b>mandatory and unbypassable</b>: the owner must restate the exact target; a missing or
/// mismatched confirmation throws <see cref="ConfirmationRequiredException"/> <b>before any dispatch</b>.
/// Centralizing the check keeps the rule in one place for every destroy verb.
/// </summary>
public static class ConfirmationGuard
{
    /// <summary>
    /// Throws unless <paramref name="confirmation"/> is an explicit, case-sensitive restatement of
    /// <paramref name="target"/>. Whitespace is trimmed; everything else must match exactly.
    /// </summary>
    /// <param name="confirmation">The owner's confirmation.</param>
    /// <param name="target">The resolved target the confirmation must restate (e.g. the spoke name).</param>
    /// <exception cref="ConfirmationRequiredException">Confirmation is missing or does not match.</exception>
    public static void RequireMatch(Confirmation confirmation, string target)
    {
        if (!confirmation.IsConfirmed ||
            !string.Equals(confirmation.RestatedTarget?.Trim(), target, StringComparison.Ordinal))
        {
            throw new ConfirmationRequiredException(target);
        }
    }
}
