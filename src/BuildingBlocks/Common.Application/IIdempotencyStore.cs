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
    /// Atomically claims the key, marking it in progress. False if it is
    /// already held.
    /// </summary>
    /// <remarks>
    /// <b>Atomically is the whole contract.</b> A read-then-write
    /// implementation admits two callers between the two operations, which is
    /// the race this behaviour exists to lose exactly once — Redis spells it
    /// <c>SET NX</c>.
    /// </remarks>
    Task<bool> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct);

    /// <summary>
    /// The entry behind a key, or null if it has expired or was never claimed.
    /// </summary>
    Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct);

    /// <summary>
    /// Records the outcome against a claimed key and re-arms the retention.
    /// </summary>
    Task CompleteAsync(string key, string payload, TimeSpan retention, CancellationToken ct);

    /// <summary>
    /// Frees a claimed key so the caller may legitimately retry.
    /// </summary>
    /// <remarks>
    /// <b>Best-effort in the implementation, and deliberately so.</b> §8.5's
    /// callout on this method is that a throw here destroys the fault the
    /// caller was already reporting: the behaviour calls this from a
    /// <c>catch</c> before <c>throw;</c>, so an exception raised here replaces
    /// a domain error with a Redis one. The implementation is where there is a
    /// logger to say the release failed; swallowing it here would be a silence
    /// with nothing to report it.
    /// </remarks>
    Task ReleaseAsync(string key, CancellationToken ct);
}

/// <summary>
/// What a claimed key holds. <paramref name="InProgress"/> distinguishes the
/// marker written on the claim from a recorded outcome — which is why
/// <paramref name="Payload"/> is nullable and why the behaviour never writes an
/// empty string for a void command.
/// </summary>
public sealed record IdempotencyEntry(bool InProgress, string? Payload);
