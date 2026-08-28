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
    /// What the marker's window must exceed <see cref="Window"/> by, because
    /// the two expiries are timed by different clocks and the marker has to
    /// outlive the claim under every skew between them.
    /// </summary>
    /// <remarks>
    /// <b>Equality is a knife-edge rather than the exact fit it reads as, and
    /// this constant is the width of the knife.</b> Three clocks decide the
    /// ordering. Redis expires the entry a duration after the completion
    /// write, which <c>CompleteAsync</c> re-arms at the commit — so the claim
    /// and the marker are started by the <em>same</em> event, and a marker
    /// window equal to <see cref="Window"/> aims both expiries at one nominal
    /// instant with no margin at all. <c>CommittedAt</c> is then stamped from
    /// the writing pod's <c>TimeProvider</c> and the purge cutoff from
    /// whichever pod runs the purge, and both services ship three replicas.
    /// A purger whose clock leads the writer's by δ deletes the marker δ
    /// early; the claim expires on schedule into a table that has already
    /// forgotten the commit, and the next retry runs the command again.
    /// <para>
    /// <b>Five minutes is chosen for the asymmetry of being wrong, not from a
    /// measurement of anybody's fleet.</b> Too generous costs a marker row
    /// kept slightly longer and a 409 that persists for the same margin; too
    /// mean is the duplicate write §8.5 exists to prevent, at a boundary set
    /// by a housekeeping setting. NTP-disciplined nodes sit orders of
    /// magnitude inside this; a node that has lost NTP is the case it is for.
    /// </para>
    /// <para>
    /// <b>It is a constant and not a setting deliberately.</b> A knob here is
    /// one whose wrong value reopens the hole silently and at a boundary
    /// nobody watches — the failure this whole mechanism is built to refuse —
    /// and the cost of the generous default is small enough that nobody needs
    /// to tune it. Deriving the marker's age from the database's clock on both
    /// the write and the purge would remove the skew rather than bound it, and
    /// is the stronger fix this constant defers
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/167">#167</see>).
    /// </para>
    /// </remarks>
    public static readonly TimeSpan SkewAllowance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The floor a marker retention window has to clear: the claim's own
    /// window plus <see cref="SkewAllowance"/>. Read by
    /// <c>RetentionPolicy.IdempotencyWindow</c> rather than restated there.
    /// </summary>
    public static TimeSpan MarkerFloor => Window + SkewAllowance;
}
