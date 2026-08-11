using Common.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The policy is service-configurable by design — §9.5 tells the reader to
/// check the inbox window against their broker's redelivery limits — so it is
/// caller-supplied, and what is caller-supplied has to be a value the type
/// refuses to hold wrongly. <c>OutboxTable</c>'s principle, applied to the
/// other registered value.
/// </summary>
/// <remarks>
/// Every one of these settings fails <em>quietly</em> when it is non-positive,
/// which is why they are refused rather than clamped or logged. A negative
/// window puts the cutoff in the future and deletes the rows just written; a
/// zero batch size or ceiling turns every pass into a no-op and stops retention
/// with nothing to see. Only the interval is loud, and it is loud on a
/// background thread inside a host that has already reported ready.
/// </remarks>
public class RetentionPolicyTests
{
    [Fact]
    public void The_defaults_are_the_documented_ones()
    {
        RetentionPolicy policy = new();

        policy.OutboxWindow.ShouldBe(TimeSpan.FromDays(7));
        policy.InboxWindow.ShouldBe(TimeSpan.FromDays(7));
        policy.BatchSize.ShouldBe(5000);
        policy.Interval.ShouldBe(TimeSpan.FromHours(1));
        policy.MaxBatchesPerPass.ShouldBe(20);
    }

    [Fact]
    public void A_window_in_the_past_is_the_only_direction_that_means_anything()
    {
        // The cutoff is `now - window`. A negative one puts it in the future,
        // so the delete matches every row including the ones written a second
        // ago — for the inbox, silently disabling deduplication at the moment
        // it is most needed.
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { OutboxWindow = TimeSpan.FromDays(-1) });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { InboxWindow = TimeSpan.FromDays(-1) });
        Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { InboxWindow = TimeSpan.Zero });
    }

    [Fact]
    public void A_batch_or_a_ceiling_of_zero_would_disable_retention_in_silence()
    {
        // `DELETE TOP (0)` deletes nothing and reports success; a ceiling of
        // zero skips the loop entirely. Either way every pass returns zero, the
        // tables grow, and the only symptom is a number nobody is watching.
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { BatchSize = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { MaxBatchesPerPass = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { BatchSize = -1 });
    }

    [Fact]
    public void A_non_positive_interval_is_refused_where_it_can_still_be_read()
    {
        // PeriodicTimer throws on this too — from ExecuteAsync, on a background
        // thread, in a host that has already reported ready. Refusing it at the
        // registration puts the failure where somebody is looking.
        Should.Throw<ArgumentOutOfRangeException>(() => new RetentionPolicy { Interval = TimeSpan.Zero });
    }

    [Fact]
    public void The_refusal_names_the_setting_that_was_wrong()
    {
        // Five settings of two shapes: a message saying only "must be positive"
        // would leave the reader to find which one, and the guard is shared.
        ArgumentOutOfRangeException thrown = Should.Throw<ArgumentOutOfRangeException>(
            () => new RetentionPolicy { MaxBatchesPerPass = 0 });

        thrown.ParamName.ShouldBe(nameof(RetentionPolicy.MaxBatchesPerPass));
    }
}
