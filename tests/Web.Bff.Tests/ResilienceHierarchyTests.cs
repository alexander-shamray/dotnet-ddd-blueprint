using Common.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Polly;
using Shouldly;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §9.7's timeout hierarchy, asserted at startup rather than trusted to
/// review. Timeouts must decrease at every level inwards: an inner timeout
/// longer than an outer one means the outer layer abandons the request while
/// the inner work continues, holding a connection and a thread for an answer
/// nobody will read.
/// </summary>
/// <remarks>
/// <b>Read off the built host, not off a helper.</b> §9.7 prints
/// <c>GetConfiguredOptions()</c> and leaves it undefined; resolving the named
/// options from the container is what makes this a test of the registration
/// rather than of a second copy of the numbers. It is also self-checking about
/// the name: <c>AddStandardResilienceHandler</c>'s options-naming convention is
/// a library detail, and asking for the wrong name returns a
/// default-constructed instance whose 30 s total request timeout fails the
/// first assertion below at once.
/// </remarks>
public class ResilienceHierarchyTests
{
    private static HttpStandardResilienceOptions Configured()
    {
        using BffFactory factory = new();

        // WebApplicationFactory builds the host lazily, and Services throws
        // until it has. CreateClient is what initialises one.
        using HttpClient client = factory.CreateClient();

        return factory.Services
            .GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>()
            .Get(PricingHop.ResilienceOptionsName);
    }

    [Fact]
    public void The_outbound_total_sits_below_the_service_operation_budget()
    {
        HttpStandardResilienceOptions options = Configured();

        // Strictly below, not at or below. Equal budgets mean the outer layer
        // gives up in the same instant the inner one would have answered, so
        // the retry that was about to succeed is discarded — the hierarchy is
        // an ordering and an ordering has no ties.
        options.TotalRequestTimeout.Timeout.ShouldBeLessThan(
            ServiceOptions.OperationTimeout,
            "§9.7: the outbound client total must be below the service operation total.");
    }

    [Fact]
    public void The_attempts_and_their_backoff_fit_inside_the_total()
    {
        HttpStandardResilienceOptions options = Configured();

        TimeSpan attempts = options.AttemptTimeout.Timeout * (options.Retry.MaxRetryAttempts + 1);

        // The waits BETWEEN attempts, not just the attempts. Omitting this
        // term is exactly what lets a configuration that overruns its own
        // ceiling pass a test written to prevent that: the attempts alone can
        // look comfortable while the waits push the real worst case past the
        // total, so the last attempt is cancelled part-way and the retry never
        // had a chance to help.
        //
        // **With UseJitter on, the nominal is not the bound.** Exponential
        // backoff from a base d over n retries sums to d × (2ⁿ − 1), and that
        // is the figure this test used — but jitter randomises each delay, and
        // Polly's decorrelated jitter was measured at 392 ms for a delay whose
        // nominal is 300 ms. A configuration relying on the nominal is relying
        // on a draw. MaxDelay is what turns it back into arithmetic, so where
        // one is set the bound is taken from it, and where one is not the test
        // says so rather than quietly trusting the nominal.
        options.Retry.MaxDelay.ShouldNotBeNull(
            "with UseJitter the nominal delay is not an upper bound, so the budget below " +
            "would be asserting a number the runtime is free to exceed (§9.7).");

        TimeSpan backoff = options.Retry.MaxDelay.Value * options.Retry.MaxRetryAttempts;

        (attempts + backoff).ShouldBeLessThanOrEqualTo(
            options.TotalRequestTimeout.Timeout,
            "the last attempt must be able to finish inside the total budget, otherwise it is " +
            "cancelled part-way and the retry never had a chance to help (§9.7).");
    }

    [Fact]
    public void The_attempt_timeout_is_inside_the_band_the_hierarchy_names()
    {
        HttpStandardResilienceOptions options = Configured();

        // §9.7's table gives 1–2 s per attempt. The lower bound matters as
        // much as the upper one: an attempt timeout small enough to cut off a
        // healthy call converts a slow dependency into a hard failure and then
        // spends the retries discovering the same thing twice more.
        options.AttemptTimeout.Timeout.ShouldBeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        options.AttemptTimeout.Timeout.ShouldBeLessThanOrEqualTo(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void TotalRequestTimeout_is_not_left_at_its_default()
    {
        HttpStandardResilienceOptions options = Configured();

        // §9.7's named trap, asserted directly rather than only as a
        // consequence. It defaults to 30 seconds, which is longer than most
        // services' own operation budget and longer than some gateway
        // timeouts — and a default is not a decision anybody can see in a
        // diff. Stated as its own test so that a failure says which property
        // was forgotten rather than that some arithmetic did not add up.
        options.TotalRequestTimeout.Timeout.ShouldNotBe(
            TimeSpan.FromSeconds(30),
            "TotalRequestTimeout is at its default, which every resilience handler must set (§9.7).");
    }

    [Fact]
    public void The_circuit_breaker_samples_over_a_window_longer_than_it_breaks_for()
    {
        HttpStandardResilienceOptions options = Configured();

        // Not part of §9.7's printed table, and it belongs to the same family
        // of arithmetic. A sampling window shorter than the break duration
        // forgets every failure while the circuit is open, so the breaker
        // closes onto a fresh window and reopens on the first error it sees —
        // a service that never recovers and never quite stays broken.
        options.CircuitBreaker.SamplingDuration.ShouldBeGreaterThan(
            options.CircuitBreaker.BreakDuration,
            "a breaker that forgets its failures while open reopens on the first error after it closes.");
    }
}
