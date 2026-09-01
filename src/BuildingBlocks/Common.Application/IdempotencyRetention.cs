namespace Common.Application;

/// <summary>
/// How long §8.5's Redis claim survives. One value, in one place, because two
/// things now depend on it and they must not be able to disagree.
/// </summary>
/// <remarks>
/// <b>It used to be a private field on
/// <see cref="IdempotencyBehavior{TCommand,TResult}"/>, and moving it out is a
/// consequence of the durable marker rather than tidying.</b> Both expire, and
/// the order they expire in is the whole of the constraint — so the marker's
/// own window has to be at least this long. While the claim is alive the key is
/// not claimable at all, so a purged marker costs nothing yet; the gap opens
/// when the claim expires with the marker already gone, and the retry after
/// that claims a free key and runs the command a second time. It is exactly the
/// difference between the two windows, at a boundary set by a retention number
/// — the least visible place a correctness property could be lost.
/// <c>RetentionPolicy</c> refuses a window below this value, and it reads the
/// value rather than restating it: a 24 written in two files is a number that
/// agrees until one of them is edited.
/// </remarks>
public static class IdempotencyRetention
{
    /// <summary>
    /// Twenty-four hours. Every entry expires — completed and in-progress
    /// alike — so §8.5's Redis guarantee is bounded in time rather than
    /// absolute, and this is the bound.
    /// </summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    /// <summary>
    /// The floor a marker retention window has to clear, and it is
    /// <see cref="Window"/> itself: equal is admitted, and is the smallest
    /// window that is. Whether it leaves a gap is a separate question the
    /// remarks answer, and the answer is conditional. Read by
    /// <c>RetentionPolicy.IdempotencyWindow</c> rather than restated there.
    /// </summary>
    /// <remarks>
    /// <b>It was <c>Window</c> plus a five-minute <c>MarkerLeadAllowance</c>,
    /// and the allowance is gone because the two terms it bounded are
    /// gone.</b> Equality read as the exact fit and was a knife-edge, for two
    /// reasons that were independent of each other and of the numbers:
    /// <para>
    /// <b>The windows did not start at the same event.</b> §6.3 stamps
    /// <c>CommittedAt</c> <em>inside</em> the transaction, before the commit;
    /// §8.5 re-armed the Redis entry to a fresh retention in
    /// <c>CompleteAsync</c>, which runs only after that transaction has
    /// returned. The claim's window therefore started later than the marker's
    /// by the commit's own tail — ordinarily milliseconds, and unbounded in
    /// principle. <c>CompleteAsync</c> now preserves what the claim had left
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/168">#168</see>),
    /// so the claim's window starts at the claim, which is <em>earlier</em>
    /// than the stamp by construction.
    /// </para>
    /// <para>
    /// <b>And they were not counted by the same clock.</b> <c>CommittedAt</c>
    /// was stamped from the writing pod's <c>TimeProvider</c> and the purge
    /// cutoff computed on whichever pod ran the purge, across §15.3's three
    /// replicas. The column now defaults to <c>SYSDATETIMEOFFSET()</c> and the
    /// purge computes its cutoff in SQL
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/167">#167</see>),
    /// so both ends are the database's clock and there is no skew term.
    /// </para>
    /// <para>
    /// <b>What is left is one ordering that holds by construction and one that
    /// holds under two assumptions, and the difference is the whole of what a
    /// reader has to carry away from here.</b> The claim is taken at <c>t0</c>
    /// and the marker is stamped at some <c>t1 &gt;= t0</c> — the same thread,
    /// the same dispatch, §6.3 running inside the claim this behaviour took.
    /// <em>That</em> is the unconditional half, and it is all the removed
    /// allowance was ever standing in for. The other half is arithmetic over
    /// two windows: the claim expires at <c>t0 + Window</c>, the marker
    /// survives until <c>t1 + IdempotencyWindow</c>, and with
    /// <c>IdempotencyWindow &gt;= Window</c> the marker outlives the claim for
    /// every value of <c>t1</c> — <b>provided the two sums are counted at the
    /// same rate, and provided <c>t1</c> falls inside the claim's own
    /// window.</b> Neither follows from <c>t1 &gt;= t0</c>, and each has a
    /// paragraph below. Where both hold, equality is admitted rather than
    /// refused — and an allowance that stood for two closed terms would now be
    /// unexplained slack, which is the shape a later reader deletes for the
    /// wrong reason.
    /// </para>
    /// <para>
    /// <b>The first assumption is that the two windows are counted at one rate,
    /// and nothing in this platform couples them.</b> The arithmetic above adds
    /// all four quantities on a single elapsed-time axis, and there is no such
    /// axis: Redis expires the claim after this <see cref="Window"/> elapsed by
    /// <em>Redis's</em> clock, while the purge deletes the marker after
    /// <c>IdempotencyWindow</c> elapsed by <em>SQL Server's</em>. So a forward
    /// step of the database's clock relative to Redis's — an NTP correction, a
    /// host migration, an operator setting the time — can carry the cutoff past
    /// the marker while the claim is still live. The margin that absorbs it is
    /// the handler's runtime plus whatever the configured window exceeds this
    /// floor by, which is six days on the shipped defaults and nothing at all
    /// at the floor itself
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171">#171</see>).
    /// </para>
    /// <para>
    /// <b>The second assumption is that the marker reaches the database inside
    /// the claim's window, and it fails with every clock in the platform
    /// correct.</b> Nothing bounds a command's runtime against
    /// <see cref="Window"/>, and the deadline is later than it first reads:
    /// §6.3 stamps the marker only after the handler has returned, after §7.5's
    /// domain-event dispatch and after §2.3's aggregate count, and the row
    /// itself does not exist until <c>SaveChangesAsync</c> sends it. **So the
    /// condition is not that the handler finished but that the INSERT
    /// committed**, and the tail between those two is the part a reader is
    /// likeliest to spend without noticing. A command whose claim expires
    /// anywhere before that commit puts <c>t1</c> beyond <c>t0 + Window</c>,
    /// and "the claim expires after the marker is stamped" is false for it.
    /// Between the expiry and the stamp the key is free with no marker behind
    /// it, which is this floor's own gap re-opened from the
    /// other end. That is §8.5's long-standing long-handler residual, where
    /// what the claim token buys is that the loser can no longer corrupt the
    /// winner's entry
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/127">#127</see>)
    /// rather than that the second attempt is refused. <b>Note the two
    /// assumptions pull opposite ways on one quantity</b>: the handler's
    /// runtime is the slack that absorbs a clock step in the paragraph above,
    /// and past <see cref="Window"/> it is itself what opens the gap.
    /// </para>
    /// <para>
    /// <b>Reinstating an allowance answers neither of them, which is why the
    /// floor is still <see cref="Window"/>.</b> Five minutes never bounded a
    /// clock step either — a step is not bounded by anything this repository
    /// can assert — so a number here would repeat the mistake in a third term
    /// rather than close it, and it bounds a handler's runtime no better. What
    /// closes the first is giving both deadlines one time source, which is a
    /// change to how the claim is stored; the second is not a retention
    /// question at all, and #127 is where it is carried.
    /// </para>
    /// <para>
    /// <b>The floor is still read rather than restated</b>, for the reason
    /// this type exists: a 24 written in two files agrees until one of them is
    /// edited. It stays a separate member even though it is now
    /// <c>Window</c> unchanged, because what it names is a
    /// <em>relationship</em> between two windows and not a duration — the next
    /// change to either is a change to it.
    /// </para>
    /// </remarks>
    public static TimeSpan MarkerFloor => Window;
}
