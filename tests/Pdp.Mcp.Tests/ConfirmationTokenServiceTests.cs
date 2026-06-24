using ModelContextProtocol;
using Pdp.Mcp.Confirm;
using Shouldly;

namespace Pdp.Mcp.Tests;

/// <summary>
/// T029/T062 — the plan→confirm token contract (Article VIII / FR-013, SC-002; clarify 2026-06-24). A token
/// binds <c>{operation, targetName}</c>, expires (~15 min), and is <b>single-use but consumed only on a
/// successful gated dispatch</b>: <see cref="ConfirmationTokenService.Check"/> validates without removing, so
/// a premature apply ("plan not ready") leaves the token redeemable; <see cref="ConfirmationTokenService.Consume"/>
/// removes it. The ~15-minute TTL covers the plan run's queue + execution + the owner's review (data-model §5).
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

    private static readonly DateTimeOffset T0 = new(2026, 6, 24, 12, 0, 0, TimeSpan.Zero);

    private static ConfirmationTokenService NewService(out FakeTime clock)
    {
        clock = new FakeTime(T0);
        return new ConfirmationTokenService(clock);
    }

    [Fact]
    public void Check_with_matching_operation_and_target_succeeds()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeVend, "app5");

        Should.NotThrow(() => service.Check(token, ConfirmationOperation.SpokeVend, "app5"));
    }

    [Fact]
    public void Check_does_not_consume_the_token_so_a_premature_apply_can_retry()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeVend, "app5");

        // Checking the token repeatedly (e.g. an apply rejected with "plan not ready", then retried) must NOT
        // burn it — only a successful gated dispatch consumes it (clarify 2026-06-24).
        Should.NotThrow(() => service.Check(token, ConfirmationOperation.SpokeVend, "app5"));
        Should.NotThrow(() => service.Check(token, ConfirmationOperation.SpokeVend, "app5"));
        Should.NotThrow(() => service.Check(token, ConfirmationOperation.SpokeVend, "app5"));
    }

    [Fact]
    public void Consume_makes_the_token_single_use()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        service.Check(token, ConfirmationOperation.SpokeDestroy, "app5");
        service.Consume(token);

        // Once consumed (the gated mutation dispatched), the token is gone — no replay.
        Should.Throw<McpException>(() => service.Check(token, ConfirmationOperation.SpokeDestroy, "app5"));
    }

    [Fact]
    public void Consume_is_idempotent_for_an_absent_token()
    {
        var service = NewService(out _);

        Should.NotThrow(() => service.Consume("never-issued"));
    }

    [Fact]
    public void Check_rejects_a_target_that_is_not_restated_verbatim()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        Should.Throw<McpException>(() => service.Check(token, ConfirmationOperation.SpokeDestroy, "app6"));
    }

    [Fact]
    public void Check_does_not_consume_the_token_on_a_target_mismatch()
    {
        var service = NewService(out _);
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        // A mistyped target must not burn a valid confirmation — the owner can still redeem it correctly.
        Should.Throw<McpException>(() => service.Check(token, ConfirmationOperation.SpokeDestroy, "app6"));
        Should.NotThrow(() => service.Check(token, ConfirmationOperation.SpokeDestroy, "app5"));
    }

    [Fact]
    public void Check_rejects_an_operation_mismatch()
    {
        var service = NewService(out _);
        // A token minted for a destroy can never release a vend (or vice versa).
        var token = service.Issue(ConfirmationOperation.SpokeDestroy, "app5");

        Should.Throw<McpException>(() => service.Check(token, ConfirmationOperation.SpokeVend, "app5"));
    }

    [Fact]
    public void Check_rejects_an_expired_token()
    {
        var service = NewService(out var clock);
        var token = service.Issue(ConfirmationOperation.FabricCreate, "westus3");

        clock.Advance(TimeSpan.FromMinutes(15) + TimeSpan.FromSeconds(1));

        Should.Throw<McpException>(() => service.Check(token, ConfirmationOperation.FabricCreate, "westus3"));
    }

    [Fact]
    public void Check_accepts_a_token_just_before_expiry()
    {
        var service = NewService(out var clock);
        var token = service.Issue(ConfirmationOperation.FabricDestroy, "westus3");

        clock.Advance(TimeSpan.FromMinutes(14) + TimeSpan.FromSeconds(59));

        Should.NotThrow(() => service.Check(token, ConfirmationOperation.FabricDestroy, "westus3"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-real-token")]
    public void Check_rejects_a_missing_or_unknown_token(string token)
    {
        var service = NewService(out _);

        Should.Throw<McpException>(() => service.Check(token, ConfirmationOperation.SpokeVend, "app5"));
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
