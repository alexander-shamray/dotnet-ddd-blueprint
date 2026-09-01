using System.Runtime.CompilerServices;
using Common.Application;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// How long processed outbox rows, handled inbox rows and §8.5's committed
/// idempotency markers are kept, and how the purge that deletes them is paced
/// (§9.4, §9.5, §8.5). Two of those windows are housekeeping and the third is
/// a correctness setting — see <see cref="IdempotencyWindow"/>, which is the
/// only one here with a floor. A registered value for
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
/// <para>
/// <b>Every member is validated, and this is <see cref="Outbox.OutboxTable"/>'s
/// principle applied to the other registered value.</b> The numbers are
/// caller-supplied by design, and what is caller-supplied has to be a value the
/// type refuses to hold wrongly. Each is refused rather than clamped because
/// each fails somewhere the reader is not looking: a negative window puts the
/// cutoff in the <em>future</em> and deletes rows written a second ago — the
/// inbox one silently disabling deduplication; a non-positive
/// <see cref="BatchSize"/> or <see cref="MaxBatchesPerPass"/> turns every pass
/// into a no-op, so retention stops with the tables growing and nothing to see;
/// and the two upper bounds exist because a value their consumers reject throws
/// on a background thread or inside a swallowed pass rather than at the
/// registration that set it.
/// </para>
/// </remarks>
public sealed record RetentionPolicy
{
    private readonly TimeSpan _outboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _inboxWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _idempotencyWindow = TimeSpan.FromDays(7);
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);
    private readonly int _batchSize = 5000;
    private readonly int _maxBatchesPerPass = 20;

    /// <summary>Processed outbox rows older than this are deleted.</summary>
    public TimeSpan OutboxWindow
    {
        get => _outboxWindow;
        init => _outboxWindow = InRange(value, MaxWindow);
    }

    /// <summary>Inbox rows handled longer ago than this are deleted.</summary>
    public TimeSpan InboxWindow
    {
        get => _inboxWindow;
        init => _inboxWindow = InRange(value, MaxWindow);
    }

    /// <summary>
    /// Idempotency markers committed longer ago than this are deleted, and
    /// this window <em>is</em> §8.5's guarantee rather than a housekeeping
    /// setting.
    /// </summary>
    /// <remarks>
    /// <b>It has a floor the other two do not, and the floor is
    /// <see cref="IdempotencyRetention.MarkerFloor"/> — the claim's own
    /// window.</b> The marker is what refuses a retry of a
    /// command that committed, and the order the two expire in is the whole of
    /// the constraint. While the Redis claim is alive the key is not claimable
    /// at all, so a purged marker costs nothing yet; the gap opens when that
    /// claim expires with the marker already gone, and the next retry then
    /// claims a free key and runs the command a second time — the duplicate
    /// write §8.5 exists to prevent, arriving at a boundary set by a retention
    /// number, which is the least visible place a correctness property could be
    /// lost.
    /// <para>
    /// <b>Matching the claim exactly is admitted, and it was refused until the
    /// two things that reordered the expiries were closed.</b> The windows did
    /// not start at the same event — <c>CommittedAt</c> is stamped inside the
    /// transaction while the claim was re-armed after it committed — and they
    /// were not counted by the same clock, the marker's age being the purging
    /// pod's against a timestamp the writing pod stamped, across three
    /// replicas. A five-minute <c>MarkerLeadAllowance</c> bounded their sum
    /// rather than removing either. §8.5's completion now preserves the claim's
    /// remaining life (#168) and the marker is stamped and aged on the database
    /// clock (#167), so <b>the claim is taken before the marker is stamped —
    /// unconditionally, the same thread inside the same dispatch</b> — and
    /// equal is then the smallest window with no gap in it.
    /// </para>
    /// <para>
    /// <b>"Then" is doing work there, and it is two assumptions rather than a
    /// connective.</b> The claim expiring before the marker is purged does not
    /// follow from the order the two were written in: the windows have to be
    /// counted at the same rate, and the handler has to finish inside the
    /// claim's window. <see cref="IdempotencyRetention.MarkerFloor"/> argues
    /// both in full. The two windows are still counted by two servers' clocks,
    /// so a forward step of the database's relative to Redis's has only the
    /// handler's runtime to be absorbed by at this floor
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171">#171</see>);
    /// and a handler outrunning that same claim is stamped after it has already
    /// expired, which is §8.5's long-handler residual
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/127">#127</see>)
    /// reaching this floor from the other end. Neither is a reason to raise the
    /// floor: a number here bounds a clock step no better than the five minutes
    /// it replaced, and bounds a runtime not at all.
    /// </para>
    /// <para>
    /// Read rather than restated, for the reason
    /// <see cref="IdempotencyRetention"/> exists: two 24s in two files agree
    /// until one of them is edited.
    /// </para>
    /// </remarks>
    public TimeSpan IdempotencyWindow
    {
        get => _idempotencyWindow;
        init => _idempotencyWindow = AtLeast(InRange(value, MaxWindow), IdempotencyRetention.MarkerFloor);
    }

    /// <summary>
    /// Rows per statement. §9.5 asks for the purge to be batched "so neither
    /// holds a long lock", and 5000 is the figure §9.4's and §9.5's arithmetic
    /// about the dispatcher's rate is written against — see
    /// <see cref="MaxBatchesPerPass"/>. Those chapters' <c>DELETE</c> samples
    /// printed the literal until they were corrected to the <c>@BatchSize</c>
    /// this parameterises, which is why the citation names the arithmetic
    /// rather than the sample.
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
        init => _interval = InRange(value, MaxInterval);
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
    /// <c>PeriodicTimer</c>'s largest accepted period, which is where
    /// <see cref="Interval"/> is actually spent.
    /// </summary>
    /// <remarks>
    /// Observed rather than read off the documentation:
    /// <c>TimeSpan.FromMilliseconds(uint.MaxValue - 1)</c> constructs a timer
    /// and <c>uint.MaxValue</c> milliseconds throws — about 49.7 days either
    /// way. Anything larger is refused here rather than by the constructor in
    /// <c>ExecuteAsync</c>, where it would throw on a background thread inside
    /// a host that had already reported ready.
    /// </remarks>
    private static readonly TimeSpan MaxInterval = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>Ten years, which is a configuration error rather than a policy.</summary>
    /// <remarks>
    /// A bound is needed at all because the outbox's and the inbox's cutoffs
    /// are <c>now - window</c> and
    /// <c>DateTimeOffset</c> subtraction throws when the result is not
    /// representable — verified with <c>TimeSpan.MaxValue</c>. The marker's
    /// cutoff is computed in SQL and so has a different ceiling, and this one
    /// clears it: ten years is 315,360,000 seconds, and <c>DATEADD</c>'s
    /// argument is an <c>int</c>. That throw
    /// lands inside <c>PurgeAsync</c>, whose caller logs and swallows, so an
    /// unbounded window buys a purge that never runs and says so once an hour
    /// in a log nobody reads. Ten years rather than the representable maximum
    /// because the two failures are different: past a decade the value is a
    /// mistake, and refusing it at the registration is worth more than
    /// tolerating it until the arithmetic gives out.
    /// </remarks>
    private static readonly TimeSpan MaxWindow = TimeSpan.FromDays(3650);

    private static TimeSpan InRange(
        TimeSpan value,
        TimeSpan maximum,
        [CallerMemberName] string member = "") =>
        value > TimeSpan.Zero && value <= maximum ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive and at most {maximum}. A retention setting outside " +
                "that range does not fail where it is set — it deletes rows that were just " +
                "written, purges nothing at all, or throws where the exception is swallowed.");

    private static TimeSpan AtLeast(
        TimeSpan value,
        TimeSpan floor,
        [CallerMemberName] string member = "") =>
        value >= floor ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be at least {floor} — how long §8.5's Redis claim survives — and " +
                "the order of the two expiries is the whole of why. A shorter window purges the " +
                "marker first; the claim then expires with nothing left to remember the commit, " +
                "so the next retry claims a free key and runs the command a second time, and the " +
                "write this platform guarantees happens once happens twice at a boundary set by " +
                "a retention setting. Matching the claim exactly is admitted, on what is " +
                "unconditional: the claim is taken before the marker is stamped, on one thread " +
                "inside one dispatch. That the marker then outlives the claim additionally " +
                "assumes the two windows are counted at one rate and that the handler finishes " +
                "inside the claim's own — see IdempotencyRetention.MarkerFloor, which argues " +
                "both. Neither is a reason to set this higher: a number here bounds a clock step " +
                "no better than the allowance it replaced, and bounds a runtime not at all.");

    private static int Positive(int value, [CallerMemberName] string member = "") =>
        value > 0 ? value
            : throw new ArgumentOutOfRangeException(
                member,
                value,
                $"{member} must be positive. A non-positive count does not fail where it is " +
                "set — it makes every pass a no-op, so retention stops with the tables growing " +
                "and nothing to see.");
}
