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
    /// <see cref="Window"/> itself: equal is the smallest window with no gap in
    /// it. Read by <c>RetentionPolicy.IdempotencyWindow</c> rather than
    /// restated there.
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
    /// <b>What is left is an ordering that holds by construction rather than
    /// by a margin.</b> The claim is taken at <c>t0</c>, the marker is stamped
    /// at some <c>t1 &gt;= t0</c>, and the claim expires at
    /// <c>t0 + Window</c> while the marker survives until
    /// <c>t1 + IdempotencyWindow</c>. With
    /// <c>IdempotencyWindow &gt;= Window</c> the marker outlives the claim for
    /// every value of <c>t1</c>, so equality is admitted rather than refused —
    /// and a margin that stood for two closed terms would now be
    /// unexplained slack, which is the shape a later reader deletes for the
    /// wrong reason.
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
