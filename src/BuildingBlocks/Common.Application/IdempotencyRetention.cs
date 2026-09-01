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
    /// window that is. What it bounds is how long §8.5's guarantee lasts rather
    /// than whether it holds — the purge refuses to delete a marker whose claim
    /// is live at any setting (ADR-039) — and the remarks argue the difference.
    /// Read by <c>RetentionPolicy.IdempotencyWindow</c> rather than restated
    /// there.
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
    /// <b>The ordering no longer rests on that arithmetic at all, and this is
    /// the part to carry away from here.</b> The claim is taken at <c>t0</c>
    /// and the marker is stamped at some <c>t1 &gt;= t0</c> — the same thread,
    /// the same dispatch, §6.3 running inside the claim this behaviour took —
    /// which is what the removed allowance was standing in for. What used to
    /// follow was a sum: the claim expires at <c>t0 + Window</c>, the marker
    /// survives until <c>t1 + IdempotencyWindow</c>, and with
    /// <c>IdempotencyWindow &gt;= Window</c> the marker outlives the claim for
    /// every value of <c>t1</c>. <b>The purge does not evaluate that sum any
    /// more.</b> It selects markers past their own window and then asks
    /// <c>IIdempotencyStore.UnheldAsync</c> which of those keys the claim store
    /// has already let go of, deleting only those — so a marker whose claim is
    /// live survives however old the row is, and no window is compared against
    /// any other
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171">#171</see>,
    /// ADR-039).
    /// </para>
    /// <para>
    /// <b>What that removed was the last term the sum could not account for:
    /// the two windows were counted at different rates.</b> Redis expires the
    /// claim after this <see cref="Window"/> elapsed by <em>Redis's</em> clock,
    /// while the purge deleted the marker after <c>IdempotencyWindow</c>
    /// elapsed by <em>SQL Server's</em>. Nothing coupled them, so a forward
    /// step of the database's clock — an NTP correction, a host migration, an
    /// operator setting the time — carried the cutoff past a marker whose claim
    /// was still live, and what absorbed it was the handler's runtime plus
    /// whatever the configured window exceeded this floor by: six days on the
    /// shipped defaults, and <em>nothing at all at the floor itself</em>. That
    /// the exposure was worst at the value this section recommends narrowing
    /// towards is why it was closed rather than documented again.
    /// </para>
    /// <para>
    /// <b>One assumption is left, and it fails with every clock in the
    /// platform correct: that the marker reaches the database inside the
    /// claim's window.</b> Nothing bounds a command's runtime against
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
    /// rather than that the second attempt is refused. It is not a retention
    /// question at all, which is why no value of this floor reaches it.
    /// </para>
    /// <para>
    /// <b>Reinstating an allowance would have answered neither, which is why
    /// the floor is still <see cref="Window"/> and why the clock term was
    /// closed at the source instead.</b> Five minutes never bounded a clock
    /// step — a step is bounded by nothing this repository can assert — so a
    /// number here would have repeated in a third term the mistake ADR-038
    /// removed from two, and it bounds a handler's runtime no better.
    /// </para>
    /// <para>
    /// <b>What the floor bounds now is how long the guarantee lasts, not
    /// whether it holds.</b> That is a smaller claim than it used to make and a
    /// checkable one. At this floor exactly, a marker becomes deletable the
    /// moment its claim expires, so <em>at most one commit per key while the
    /// marker survives</em> ends where the claim does; above it, the marker
    /// outlives the claim by the difference and the guarantee runs that much
    /// longer. Neither is a gap — the purge cannot delete a marker whose claim
    /// is live at any setting — so what an operator is choosing here is the
    /// length of the promise rather than its truth, which is the distinction
    /// this floor could not draw while it was the thing making the ordering
    /// true.
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
