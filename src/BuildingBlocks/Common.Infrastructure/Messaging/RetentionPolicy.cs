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
    /// <summary>Processed outbox rows older than this are deleted.</summary>
    public TimeSpan OutboxWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Inbox rows handled longer ago than this are deleted.</summary>
    public TimeSpan InboxWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Rows per statement. §9.5 asks for the purge to be batched "so neither
    /// holds a long lock", and 5000 is what §9.4's and §9.5's <c>DELETE TOP</c>
    /// samples both write.
    /// </summary>
    public int BatchSize { get; init; } = 5000;

    /// <summary>
    /// How often a pass runs. Slow on purpose: retention is a housekeeping
    /// concern measured in days, and a purge competing with the dispatcher's
    /// twice-a-second claim for the same table's locks buys nothing.
    /// </summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Batches per table per pass. A ceiling rather than a target: without one,
    /// a first run against a table that was never purged loops until it is
    /// empty, holding a connection and competing with the dispatcher for as
    /// long as that takes. With it the backlog drains over several passes and
    /// each pass is bounded — 20 × 5000 is 100,000 rows an hour per table,
    /// comfortably past any rate the dispatcher can create them at.
    /// </summary>
    public int MaxBatchesPerPass { get; init; } = 20;
}
