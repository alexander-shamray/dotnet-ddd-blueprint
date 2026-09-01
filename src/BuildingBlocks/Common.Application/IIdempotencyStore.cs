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
/// a constraint on what this port can be asked to decide rather than an
/// implementation detail. No ordering of a claim in this store against a SQL
/// commit is atomic, so nothing here can tell a fault that rolled back from one
/// raised over work that committed and lost its acknowledgement.
/// </para>
/// <para>
/// <b>That case is answered one port over.</b>
/// <see cref="IIdempotencyMarkerStore"/> writes a row inside the transaction
/// and §6.3 reads it before the handler runs, which is what took the exception
/// out of this section's guarantee — at most one commit per key while the
/// marker survives, rather than within this store's retention and only then.
/// This store keeps the half it is good at: the atomic claim that makes a
/// concurrent duplicate fail early, and the recorded outcome a replay hands
/// back.
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
    /// Records the outcome against a key this claim still owns, <b>preserving
    /// the claim's remaining life rather than starting a new one</b>. Does
    /// nothing if the claim has expired or been superseded.
    /// </summary>
    /// <remarks>
    /// <b>Silent on a lost claim, and that is the safe direction.</b> The
    /// alternative is throwing over a command that has already committed,
    /// which would turn a lost replay into a fault the caller can act on
    /// wrongly. An implementation is where there is a logger, so the loss is
    /// reported rather than absorbed.
    /// <para>
    /// <b>There is no retention parameter, and its absence is the contract
    /// rather than a simplification.</b> This took one and re-armed the entry
    /// to a full window, which started the claim's life at the <em>commit</em>
    /// — later than the durable marker §6.3 stamps inside the transaction that
    /// precedes it, by the commit's own tail. The marker then had to be kept
    /// for a margin covering a lag nothing bounds. Preserving what the claim
    /// had left starts its window at <see cref="TryClaimAsync"/>, which is
    /// earlier than the stamp by construction, so the <em>start</em> events
    /// are ordered without a margin
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/168">#168</see>).
    /// </para>
    /// <para>
    /// <b>What that costs is stated in §8.5 rather than hidden here.</b> An
    /// outcome stays replayable for the remainder of the window the claim
    /// opened, so a slow command shortens its own replay window by however
    /// long it ran. What it buys unconditionally is the ordering of the two
    /// <em>start</em> events, and that is the whole of what this contract
    /// promises: the claim's window opens at <see cref="TryClaimAsync"/> and
    /// §6.3 stamps its marker at some later instant, on the same thread inside
    /// the same dispatch.
    /// </para>
    /// <para>
    /// <b>That the marker then outlives the claim is not promised here, on two
    /// counts independent of each other.</b> The two windows are counted by two
    /// servers' clocks — this one's by Redis, the marker's by SQL Server — so a
    /// forward step of the database's relative to Redis's can purge the marker
    /// while the claim is still live
    /// (<see href="https://github.com/alexander-shamray/dotnet-ddd-blueprint/issues/171">#171</see>).
    /// And a handler that outruns the retention it was claimed with reaches
    /// this method with the claim already expired, so the stamp lands after the
    /// claim has gone for reasons no clock is involved in — §8.5's long-handler
    /// residual, whose damage the claim token bounds rather than removes
    /// (#127). Both are argued in full on
    /// <see cref="IdempotencyRetention.MarkerFloor"/>; a consumer reading this
    /// contract alone should assume neither.
    /// </para>
    /// </remarks>
    Task CompleteAsync(string key, string claim, string payload, CancellationToken ct);

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
