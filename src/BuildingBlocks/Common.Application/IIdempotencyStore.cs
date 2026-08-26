namespace Common.Application;

/// <summary>
/// The claim store behind <see cref="IdempotencyBehavior{TCommand,TResult}"/>,
/// as a port. The behaviour lives in this assembly, which §4.2 forbids to
/// reference <c>StackExchange.Redis</c> — exactly as it forbids
/// <see cref="IUnitOfWork"/> to reference EF Core — so the only Redis in §8.5
/// is on the far side of this interface.
/// </summary>
/// <remarks>
/// <b>The key this takes is a shape, not a keyspace.</b> §8.3's rule is that a
/// call site writes only half the key: the implementation owns the
/// <c>{service}:idem:</c> prefix, and passing an already-prefixed key here
/// would double it. The behaviour therefore builds
/// <c>{subject}:{operation}:{commandId}</c> and nothing more.
/// <para>
/// <b>A key names the work; a claim token names the attempt.</b> Both are
/// needed, and #127 is what it cost to carry only the first: every write and
/// every delete has to prove it still owns the key it is acting on, or an
/// attempt that outlived its own claim silently clobbers its successor's.
/// </para>
/// <para>
/// <b>Every method here is outside the command's transaction</b>, and that is
/// the constraint the whole of §8.5's residual section is about rather than an
/// implementation detail. No ordering of a claim in this store against a SQL
/// commit is atomic, so the guarantee is bounded — at most one commit per key
/// within the retention, except across a lost commit acknowledgement.
/// </para>
/// </remarks>
public interface IIdempotencyStore
{
    /// <summary>
    /// Atomically claims the key, marking it in progress. Returns the claim
    /// token on success and null if the key is already held.
    /// </summary>
    /// <remarks>
    /// <b>Atomically is the whole contract.</b> A read-then-write
    /// implementation admits two callers between the two operations, which is
    /// the race this behaviour exists to lose exactly once — Redis spells it
    /// <c>SET NX</c>.
    /// <para>
    /// <b>The token is what the other two methods prove ownership with.</b>
    /// A claim used to return only whether the key was taken and not
    /// <i>which</i> attempt took it, so <see cref="CompleteAsync"/> and
    /// <see cref="ReleaseAsync"/> wrote and deleted unconditionally: an
    /// attempt outliving <paramref name="retention"/> overwrote or deleted its
    /// successor's live claim, and permitted a second commit inside that
    /// successor's retention. That is #127, and the token closes it — an
    /// implementation must compare and act in one atomic step, exactly as
    /// <c>IDistributedLock</c>'s release does, because a check and an act that
    /// are two operations are two operations a claim can expire between.
    /// </para>
    /// <para>
    /// <b>What the token does not close is the claim expiring under a running
    /// handler.</b> Nothing here bounds the retention against a handler's
    /// runtime, and the shipped caller passes 24 hours. Past that window a
    /// successor may claim the key and both attempts run; what the token buys
    /// is that the loser can no longer corrupt the winner's entry — it fails
    /// to complete rather than completing over the top. The residual is stated
    /// in §8.5 and is a property of the retention, not of this signature.
    /// </para>
    /// </remarks>
    Task<string?> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct);

    /// <summary>
    /// The entry behind a key, or null if it has expired or was never claimed.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not token-checked.</b> This answers a question about the
    /// key rather than about an attempt, and its one caller is the path that
    /// holds no claim: the caller that lost the race and is deciding whether to
    /// replay an answer or refuse. A token it does not have cannot be a
    /// parameter here.
    /// </remarks>
    Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct);

    /// <summary>
    /// Records the outcome against a key this claim still owns and re-arms the
    /// retention. Does nothing if the claim has expired or been superseded.
    /// </summary>
    /// <remarks>
    /// <b>Silent on a lost claim, and that is the safe direction.</b> The
    /// alternative is throwing over a command that has already committed,
    /// which would turn a lost replay into a fault the caller can act on
    /// wrongly. An implementation is where there is a logger, so the loss is
    /// reported rather than absorbed.
    /// </remarks>
    Task CompleteAsync(string key, string claim, string payload, TimeSpan retention, CancellationToken ct);

    /// <summary>
    /// Frees a key this claim still owns so the caller may legitimately retry.
    /// Does nothing if the claim has expired or been superseded.
    /// </summary>
    /// <remarks>
    /// <b>Best-effort in the implementation, and deliberately so.</b> §8.5's
    /// callout on this method is that a throw here destroys the fault the
    /// caller was already reporting: the behaviour calls this from a
    /// <c>catch</c> before <c>throw;</c>, so an exception raised here replaces
    /// a domain error with a Redis one. The implementation is where there is a
    /// logger to say the release failed; swallowing it here would be a silence
    /// with nothing to report it.
    /// <para>
    /// <b>The token is why this may delete at all.</b> An unconditional delete
    /// of a key whose claim has moved on frees a successor's claim while that
    /// successor is still running, which is the half of #127 that is worse in
    /// kind than the overwrite: it admits a concurrent duplicate rather than
    /// corrupting a record of one.
    /// </para>
    /// </remarks>
    Task ReleaseAsync(string key, string claim, CancellationToken ct);
}

/// <summary>
/// What a claimed key holds. <paramref name="InProgress"/> distinguishes the
/// marker written on the claim from a recorded outcome — which is why
/// <paramref name="Payload"/> is nullable and why the behaviour never writes an
/// empty string for a void command.
/// </summary>
public sealed record IdempotencyEntry(bool InProgress, string? Payload);
