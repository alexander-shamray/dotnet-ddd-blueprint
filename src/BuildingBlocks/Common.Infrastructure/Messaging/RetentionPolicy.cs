using System.Runtime.CompilerServices;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// How long processed outbox rows and handled inbox rows are kept, and how the
/// purge that deletes them is paced (§9.4, §9.5). A registered value for
/// <see cref="Outbox.OutboxTable"/>'s reason one indirection over: the numbers
/// are a service's to choose, and a <c>const</c> in common code is a choice made
/// once for everybody.
/// </summary>
/// <remarks>
/// <b>The inbox window is a real constraint, not a round number.</b> §9.5 states
/// it: the window must exceed the broker's longest possible redelivery delay,
/// including time a message spends in the error queue before being replayed.
/// Pruning sooner lets a late redelivery through as if it were new — exactly the
/// duplicate the table exists to stop. Seven days is a starting point to check
/// against RabbitMQ's configured limits, and a number a chapter tells the reader
/// to check is a number the code has to let them change.
/// <para>
/// The outbox window is the softer of the two — processed rows are kept for
/// debugging (§9.4) — but its <em>predicate</em> is not soft at all, and lives
/// in <see cref="RetentionPurgeService"/> rather than here.
/// </para>
/// </remarks>
public sealed record RetentionPolicy
{
    private readonly TimeSpan _outboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _inboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly int _batchSize = 5000;
    private readonly int _maxBatchesPerPass = 20;

    /// <summary>Processed outbox rows older than this are deleted.</summary>
    public TimeSpan OutboxWindow
    {
        get => _outboxWindow;
        init => _outboxWindow = Positive(value);
    }

    /// <summary>Inbox rows handled longer ago than this are deleted.</summary>
    public TimeSpan InboxWindow
    {
        get => _inboxWindow;
        init => _inboxWindow = Positive(value);
    }

    /// <summary>
    /// Rows per statement. §9.5 asks for the purge to be batched "so neither
    /// holds a long lock", and 5000 is what §9.4's and §9.5's <c>DELETE TOP</c>
    /// samples both write.
    /// </summary>
    public int BatchSize
    {
        get => _batchSize;
        init => _batchSize = Positive(value);
    }

    /// <summary>
    /// How often a pass runs. Slow on purpose: retention is a housekeeping
    /// concern measured in days, and a purge competing with the dispatcher's
    /// twice-a-second claim for the same table's locks buys nothing.
    /// </summary>
    public TimeSpan Interval
    {
        get => _interval;
        init => _interval = Positive(value);
    }

    /// <summary>
    /// Batches per table per pass. A ceiling rather than a target: without one,
    /// a first run against a table that was never purged loops until it is
    /// empty, holding a connection and competing with the dispatcher for as
    /// long as that takes. With it the backlog drains over several passes and
    /// each pass is bounded.
    /// </summary>
    /// <remarks>
    /// <b>The bound is real and it is below the dispatcher's, which is the
    /// opposite of what it looks like.</b> Twenty batches of 5,000 an hour is
    /// 100,000 rows per table — about 28 a second — where <c>OutboxDispatcher</c>
    /// claims up to 100 rows twice a second, so a service sustaining its full
    /// delivery rate produces processed rows some seven times faster than this
    /// reclaims them. That is not a competition at ordinary load, because a row
    /// is only purgeable a week after it was processed and a week of backlog is
    /// what <see cref="OutboxWindow"/> is for; it is one at sustained peak, and
    /// the answer there is a shorter <see cref="Interval"/> or a larger ceiling
    /// rather than a different design — §13.6's outbox-growth alert is what
    /// makes the need visible before the table does.
    /// </remarks>
    public int MaxBatchesPerPass
    {
        get => _maxBatchesPerPass;
        init => _maxBatchesPerPass = Positive(value);
    }

    /// <summary>
    /// Every member of this type is a positive quantity, and each of them
    /// drives something that fails differently when it is not.
    /// </summary>
    /// <remarks>
    /// This is <see cref="Outbox.OutboxTable"/>'s principle applied to the
    /// other registered value: a policy is service-configurable by design
    /// (§9.5 tells the reader to check the inbox window against their broker),
    /// so it is caller-supplied, and what is caller-supplied has to be a value
    /// the type refuses to hold wrongly.
    /// <para>
    /// The failures are worth naming because none of them throws. A negative
    /// window puts the cutoff in the <em>future</em> and deletes the rows that
    /// were just written — the inbox one silently disabling deduplication.
    /// A zero <see cref="MaxBatchesPerPass"/> or <see cref="BatchSize"/> turns
    /// every pass into a no-op, so retention stops with the tables growing and
    /// nothing to see. Only a non-positive <see cref="Interval"/> is loud, and
    /// it is loud in the wrong place: <c>PeriodicTimer</c> throws on a
    /// background thread inside a host that has already reported ready.
    /// </para>
    /// </remarks>
    private static TimeSpan Positive(TimeSpan value, [CallerMemberName] string member = "") =>
        value > TimeSpan.Zero ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive. A non-positive retention setting does not fail — " +
                "it deletes rows that were just written, or purges nothing at all.");

    private static int Positive(int value, [CallerMemberName] string member = "") =>
        value > 0 ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive. A non-positive retention setting does not fail — " +
                "it deletes rows that were just written, or purges nothing at all.");
}
