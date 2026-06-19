using ModelContextProtocol;
using Pdp.Mcp.Confirm;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// T029 — the plan→confirm token contract (Article VIII / FR-013, SC-002): a token is single-use, expires
/// (~5 min), and binds <c>{operation, targetName}</c> so an apply/destroy is rejected unless it restates the
/// exact target with the token from its own Plan… call (data-model §5).
/// </summary>
public sealed class ConfirmationTokenServiceTests
{
    // A controllable clock — Microsoft.Extensions.TimeProvider.Testing is not on the pinned set, so a tiny
    // mutable TimeProvider drives the expiry assertions deterministically.
    private sealed class FakeTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly DateTimeOffset T0 = new(2026, 6, 18, 12, 0, 0, TimeSpan.Zero);

    private static ConfirmationTokenService NewService(out FakeTime clock)
    {
        clock = new FakeTime(T0);
        return new ConfirmationTokenService(clock);
    }

    [Fact]
    public void Validate_with_matching_operation_and_target_succeeds_once()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeVend, "app5");

        // First redemption matches → no throw.
        Should.NotThrow(() => service.Validate(token, ConfirmationOperation.SpokeVend, "app5"));
    }

    [Fact]
    public void Validate_is_single_use()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        service.Validate(token, ConfirmationOperation.SpokeDestroy, "app5");

        // Second redemption of a consumed token is rejected (no replay).
        Should.Throw<McpException>(() => service.Validate(token, ConfirmationOperation.SpokeDestroy, "app5"));
    }

    [Fact]
    public void Validate_rejects_a_target_that_is_not_restated_verbatim()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        Should.Throw<McpException>(() => service.Validate(token, ConfirmationOperation.SpokeDestroy, "app6"));
    }

    [Fact]
    public void Validate_does_not_consume_the_token_on_a_target_mismatch()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        // A mistyped target must not burn a valid confirmation — the owner can still redeem it correctly.
        Should.Throw<McpException>(() => service.Validate(token, ConfirmationOperation.SpokeDestroy, "app6"));
        Should.NotThrow(() => service.Validate(token, ConfirmationOperation.SpokeDestroy, "app5"));
    }

    [Fact]
    public void Validate_rejects_an_operation_mismatch()
    {
        var service = NewService(out _);
        // A token minted for a destroy can never release a vend (or vice versa).
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        Should.Throw<McpException>(() => service.Validate(token, ConfirmationOperation.SpokeVend, "app5"));
    }

    [Fact]
    public void Validate_rejects_an_expired_token()
    {
        var service = NewService(out var clock);
        var token = service.Issue(ConfirmationOperation.FabricCreate, "westus3");

        clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        Should.Throw<McpException>(() => service.Validate(token, ConfirmationOperation.FabricCreate, "westus3"));
    }

    [Fact]
    public void Validate_accepts_a_token_just_before_expiry()
    {
        var service = NewService(out var clock);
        var token = service.Issue(ConfirmationOperation.FabricDestroy, "westus3");

        clock.Advance(TimeSpan.FromMinutes(4) + TimeSpan.FromSeconds(59));

        Should.NotThrow(() => service.Validate(token, ConfirmationOperation.FabricDestroy, "westus3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public void Validate_rejects_a_missing_or_unknown_token(string token)
    {
        var service = NewService(out _);

        Should.Throw<McpException>(() => service.Validate(token, ConfirmationOperation.SpokeVend, "app5"));
    }

    [Fact]
    public void Issued_tokens_are_unique_and_opaque()
    {
        var service = NewService(out _);

        var a = service.Issue(ConfirmationOperation.SpokeVend, "app5");
        var b = service.Issue(ConfirmationOperation.SpokeVend, "app5");

        a.ShouldNotBe(b);
        a.ShouldNotContain("app5");
    }
}
