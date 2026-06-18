using FluentValidation;
using Pdp.ControlPlane.Verbs.Model;

namespace Pdp.ControlPlane.Verbs.Validation;

/// <summary>
/// Validates a <see cref="FabricCreateRequest"/> before the verb runs (FR-023; no prohibited deps —
/// FluentValidation). Shape only: the region matches the platform's naming and the region index is in
/// the ledger's geographic range (1–255; index 0 is the platform supernet). Live preconditions
/// (region not already registered under a different index, supernet non-overlap) are enforced by the
/// ledger at <c>RegisterRegionAsync</c>.
/// </summary>
public sealed class FabricCreateRequestValidator : AbstractValidator<FabricCreateRequest>
{
    /// <summary>Configures the rules.</summary>
    public FabricCreateRequestValidator()
    {
        RuleFor(r => r.Region)
            .NotEmpty()
            .Matches("^[a-z0-9]+$")
            .WithMessage("'Region' must be a lowercase Azure region id, e.g. westus3.");

        RuleFor(r => r.RegionIndex)
            .InclusiveBetween(1, 255)
            .WithMessage("'RegionIndex' must be 1–255 (the 2nd octet of the region's /16); index 0 is the platform supernet.");
    }
}
