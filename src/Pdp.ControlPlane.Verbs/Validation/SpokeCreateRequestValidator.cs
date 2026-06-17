using FluentValidation;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Validation;

/// <summary>
/// Validates a <see cref="SpokeCreateRequest"/> before the verb runs (FR-023; no prohibited deps —
/// FluentValidation). Shape only: the subscription is a GUID, the region/name match the platform's
/// naming, and the requested block size is inside the IPAM spoke range (<c>/22</c>–<c>/29</c>). Live
/// preconditions (region registered, address space available) are enforced by the ledger at allocate.
/// There is deliberately <b>no CIDR rule</b> — the block is allocated, never supplied (Gate-G1).
/// </summary>
public sealed class SpokeCreateRequestValidator : AbstractValidator<SpokeCreateRequest>
{
    /// <summary>The spoke-name pattern accepted by <c>spoke-vend.yml</c> (lowercase, digits, hyphen).</summary>
    public const string NamePattern = "^[a-z0-9-]+$";

    /// <summary>Configures the rules.</summary>
    public SpokeCreateRequestValidator()
    {
        RuleFor(r => r.Subscription)
            .NotEmpty()
            .Must(s => Guid.TryParse(s, out _))
            .WithMessage("'Subscription' must be an Azure subscription GUID.");

        RuleFor(r => r.Region)
            .NotEmpty()
            .Matches("^[a-z0-9]+$")
            .WithMessage("'Region' must be a lowercase Azure region id, e.g. westus3.");

        RuleFor(r => r.Name)
            .NotEmpty()
            .MaximumLength(24)
            .Matches(NamePattern)
            .WithMessage("'Name' must be 1–24 chars of [a-z0-9-] (the spoke-vend naming rule).");

        RuleFor(r => r.Size)
            .InclusiveBetween(22, 29)
            .WithMessage("'Size' must be a spoke prefix length between /22 and /29.");
    }
}
