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
    /// How far the marker's expiry must lead the claim's. The marker is what
    /// refuses a retry once the claim is gone, so it has to outlive the claim
    /// under everything that can reorder the two — and two independent things
    /// can.
    /// </summary>
    /// <remarks>
    /// <b>Equality is a knife-edge rather than the exact fit it reads as, and
    /// this constant is the width of the knife.</b> A marker window equal to
    /// <see cref="Window"/> looks like the tightest safe value and is not safe
    /// at all, because the two expiries are neither started by the same event
    /// nor counted by the same clock.
    /// <para>
    /// <b>The first term is the lag between the two writes, and it is not
    /// zero.</b> §6.3 stamps <c>CommittedAt</c> with <c>MarkAsync</c> <em>
    /// inside</em> the transaction, before <c>SaveChangesAsync</c>; §8.5
    /// re-arms the Redis entry in <c>CompleteAsync</c> only after
    /// <c>next()</c> has returned, which is after the commit. So the claim's
    /// window starts <em>later</em> than the marker's by the commit's own tail
    /// — ordinarily milliseconds, and unbounded in principle, since a
    /// suspension or a stalled connection between those two points stretches
    /// it. Redis then outlives the marker by exactly that lag even with
    /// perfectly synchronised clocks.
    /// </para>
    /// <para>
    /// <b>The second term is clock skew, and it is a different pair of
    /// clocks.</b> <c>CommittedAt</c> is stamped from the writing pod's
    /// <c>TimeProvider</c> and the purge cutoff from whichever pod runs the
    /// purge; §15.3 ships three replicas of each service. A purger whose clock
    /// leads the writer's by δ deletes the marker δ early. Either term alone
    /// lets the claim expire into a table that has already forgotten the
    /// commit, and the next retry then runs the command a second time.
    /// </para>
    /// <para>
    /// <b>Five minutes is chosen for the asymmetry of being wrong, not from a
    /// measurement of anybody's fleet.</b> Too generous costs a marker row
    /// kept slightly longer and a 409 that persists for the same margin; too
    /// mean is the duplicate write §8.5 exists to prevent, at a boundary set
    /// by a housekeeping setting. A commit tail and an NTP-disciplined clock
    /// both sit orders of magnitude inside it; a stalled process and a node
    /// that has lost NTP are the cases it is for, and it bounds their
    /// <em>sum</em> rather than either separately.
    /// </para>
    /// <para>
    /// <b>It is a constant and not a setting deliberately.</b> A knob here is
    /// one whose wrong value reopens the hole silently and at a boundary
    /// nobody watches — the failure this whole mechanism is built to refuse —
    /// and the cost of the generous default is small enough that nobody needs
    /// to tune it.
    /// </para>
    /// <para>
    /// <b>Neither term is closed here, and they need different fixes.</b>
    /// Ageing the row from the database's clock on both the write and the
    /// purge removes the skew term
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/167">#167</see>)
    /// and does nothing about the lag; the lag closes by not re-arming the
    /// claim at completion, which is a change to what §8.5 promises a caller
    /// about how long an outcome stays replayable
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/168">#168</see>).
    /// Until both land this margin is what stands between them and a duplicate
    /// write.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MarkerLeadAllowance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The floor a marker retention window has to clear: the claim's own
    /// window plus <see cref="MarkerLeadAllowance"/>. Read by
    /// <c>RetentionPolicy.IdempotencyWindow</c> rather than restated there.
    /// </summary>
    public static TimeSpan MarkerFloor => Window + MarkerLeadAllowance;
}
