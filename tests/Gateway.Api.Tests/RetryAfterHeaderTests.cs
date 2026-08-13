using Shouldly;
using Xunit;

namespace Gateway.Api.Tests;

/// <summary>
/// §10.3's <c>Retry-After</c> rounding, pinned where it can actually be
/// reached.
/// </summary>
/// <remarks>
/// The sub-second row is the one this file exists for: it is the only input
/// that distinguishes rounding up from the truncating cast the handler used
/// until Copilot found it, and no HTTP-level assertion in this suite reaches
/// it — the anonymous window is a minute long, so a rejection carries tens of
/// seconds and both spellings agree. <c>The_anonymous_window_admits_its_budget…</c>
/// passed with the bug in place, which is the measurement that moved the rule
/// out of the pipeline and into a type.
/// </remarks>
public sealed class RetryAfterHeaderTests
{
    [Theory]
    // The defect, and the whole reason for the type: truncation says "retry
    // now" while the limiter is still refusing.
    [InlineData(0.2, 1)]
    [InlineData(0.8, 1)]
    // Whole seconds are unchanged — rounding up must not inflate a value that
    // is already exact, or every 429 advertises a second it does not need.
    [InlineData(1.0, 1)]
    [InlineData(59.0, 59)]
    // The ordinary case, where the cast and the ceiling disagree by one and
    // only the ceiling is safe.
    [InlineData(59.2, 60)]
    // A lease already expired by the time the handler runs. Zero here is the
    // truthful answer — the limiter is no longer refusing — where a negative
    // would be a header no client can parse.
    [InlineData(0.0, 0)]
    [InlineData(-0.5, 0)]
    public void Whole_seconds_are_rounded_up_never_down(double remaining, int expected) =>
        RetryAfterHeader.Seconds(TimeSpan.FromSeconds(remaining)).ShouldBe(expected);
}
