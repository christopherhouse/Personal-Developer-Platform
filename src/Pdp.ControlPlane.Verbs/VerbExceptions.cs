using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs;

/// <summary>
/// A destroy (or other gated mutation) was requested without the explicit, matching confirmation the
/// constitution mandates (Article VIII / FR-007). The verb layer throws this <b>before any dispatch</b>,
/// so a missing or mismatched confirmation can never reach the execution plane. Unbypassable: there is
/// no <c>--yes</c> shortcut for destroy.
/// </summary>
public sealed class ConfirmationRequiredException : Exception
{
    /// <summary>Creates the exception describing the target whose confirmation was missing/mismatched.</summary>
    public ConfirmationRequiredException(string target)
        : base($"This operation requires an explicit confirmation restating the target '{target}' (Article VIII / FR-007). No mutation was dispatched.")
    {
        Target = target;
    }

    /// <summary>The target whose confirmation was required.</summary>
    public string Target { get; }
}

/// <summary>
/// A verb referenced an environment that the registry does not know — the operation cannot proceed
/// (fail-fast, no dispatch — FR-023).
/// </summary>
public sealed class EnvironmentNotFoundException : Exception
{
    /// <summary>Creates the exception for an unresolved <see cref="EnvRef"/>.</summary>
    public EnvironmentNotFoundException(EnvRef reference)
        : base(Describe(reference))
    {
        Reference = reference;
    }

    /// <summary>The reference that did not resolve.</summary>
    public EnvRef Reference { get; }

    private static string Describe(EnvRef reference) =>
        reference.EnvId is { } id
            ? $"No environment with env_id '{id}' is registered."
            : $"No environment '{reference.Name}' ({reference.Kind}) is registered in subscription '{reference.Subscription}'.";
}
