namespace Common.Application;

/// <summary>
/// Carries the idempotency key from §8.5's behaviour to §6.3's transaction, for
/// the one scope that dispatched the command. Scoped, like everything else on
/// the command path.
/// </summary>
/// <remarks>
/// <b>A carried value rather than a rebuilt one, because the key has a shape
/// nobody should have two copies of.</b>
/// <see cref="IdempotencyBehavior{TCommand,TResult}"/> builds
/// <c>{subject}:{operation}:{commandId}</c> out of a principal it binds from
/// <see cref="ICurrentUser"/> and a <c>static abstract</c> member reachable
/// only through its own <see cref="IIdempotentCommand"/> constraint —
/// <c>TransactionBehavior</c> is constrained to neither and would have to
/// reach both by reflection. Two implementations of one key shape is the
/// "hand-written double is a second specification" failure with the two copies
/// one file apart.
/// <para>
/// <b>It is empty for every command that did not opt in</b>, which is the
/// signal §6.3 acts on: no key, no marker. That is the same fail-open shape
/// §8.5 already names — a command that forgets <see cref="IIdempotentCommand"/>
/// is simply never protected — so it is gated the same way, by the per-service
/// reflection test over the shape of every command rather than by trusting the
/// author.
/// </para>
/// <para>
/// <b>The key is cleared when the dispatch that set it unwinds, and that is a
/// correctness requirement rather than tidiness.</b> A scope is not promised to
/// serve one command: an endpoint or an integration-event handler may dispatch
/// twice, and nothing in this type, the container or the tests could enforce
/// otherwise. A key left standing would be captured by the *next* command's
/// transaction — which finds the first command's marker and refuses a command
/// nobody ever protected, or, where the first attempt failed, writes a marker
/// naming the wrong command's work. Neither is reachable in this platform
/// today, and that is exactly the kind of premise the next caller falsifies:
/// <see cref="IdempotencyBehavior{TCommand,TResult}"/> clears in a
/// <c>finally</c>, so the key lives for precisely the dispatch that claimed it.
/// </para>
/// <para>
/// <b>The nested-dispatch hazard is real and is closed at the reader, not
/// here.</b> A command dispatched from inside a command handler would run its
/// own <c>IdempotencyBehavior</c> and overwrite <see cref="Key"/> while the
/// outer transaction is still open, so an outer behaviour re-reading this
/// property after <c>next()</c> would mark the inner command's key against the
/// outer command's rows. §6.3 therefore reads it once, before anything runs.
/// Nothing in this platform dispatches a command from a command handler and
/// <c>No_command_handler_dispatches_a_command</c> asserts so per service; the
/// capture is what keeps that gate's failure a stopped build rather than a
/// wrong marker.
/// </para>
/// </remarks>
public sealed class IdempotencyContext
{
    /// <summary>The key claimed for this scope's command, or null if none was.</summary>
    public string? Key { get; private set; }

    /// <summary>Records the key §8.5's behaviour claimed.</summary>
    public void Claim(string key) => Key = key;

    /// <summary>
    /// Forgets it, when the dispatch that claimed it unwinds. Called from a
    /// <c>finally</c>, so a fault leaves nothing behind either.
    /// </summary>
    public void Clear() => Key = null;
}
