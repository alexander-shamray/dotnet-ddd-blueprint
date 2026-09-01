using System.Reflection;
using System.Text.Json;

namespace Common.Application;

/// <summary>
/// §8.5's claim-before-work protection, seated between
/// <see cref="ValidationBehavior{TCommand,TResult}"/> and
/// <see cref="TransactionBehavior{TCommand,TResult}"/> (§6.3). What §8.5 buys
/// is <b>at most one commit per key while the marker survives</b>, and only
/// part of that is this type's doing: the claim below is the atomic exclusion
/// that makes a concurrent duplicate fail early, and the durable half is
/// <see cref="IIdempotencyMarkerStore"/>, written and read by §6.3 inside the
/// transaction ([ADR-037]).
/// <para>
/// That sentence used to end <i>within <see cref="Retention"/>, except across a
/// lost commit acknowledgement</i>, and both qualifiers were this behaviour's.
/// The marker retired them together, because a row outlives the claim and can
/// be read where no Redis entry can be trusted.
/// </para>
/// </summary>
/// <remarks>
/// <b>Constrained to <see cref="IIdempotentCommand"/>, so it fails open.</b> A
/// command that does not opt in is dispatched with no protection and no
/// diagnostic — the container omits an open-generic registration whose
/// constraints the closed type does not satisfy, silently, which was measured
/// against this package pin rather than assumed. That is the same shape as an
/// unregistered handler (§6.2), so it gets the same kind of guard: a reflection
/// test over the shape of the command, not a review comment.
/// <para>
/// <b>The second constraint fails open the same way and is easier to miss.</b>
/// <c>where TResult : Result</c> means a command returning anything else is
/// unprotected too. Nothing here can detect that; the gate is in the test
/// suite — <c>Idempotent_commands_return_a_result_shape_the_behaviour_rebuilds</c>,
/// one per service, named for the shape rather than for replayability
/// because §8.5 makes that distinction and the test only establishes the
/// first.
/// <b>This sentence named a gate that did not exist until a review asked for
/// it</b>, which is the failure it was written to prevent, arriving in the
/// comment rather than in the code: both suites checked the interface opt-in
/// and the operation name, and neither looked at the return type.
/// </para>
/// </remarks>
public sealed class IdempotencyBehavior<TCommand, TResult>(
    IIdempotencyStore store,
    ICurrentUser currentUser,
    IdempotencyContext idempotency)
    : IPipelineBehavior<TCommand, TResult>
    where TCommand : ICommand<TResult>, IIdempotentCommand
    where TResult : Result
{
    /// <summary>
    /// How long a claim survives, from <see cref="IdempotencyRetention"/>.
    /// Every entry expires, completed and in progress alike, so a retry
    /// arriving after this claims a free key — and what stops it committing a
    /// second time is the durable marker §6.3 writes, whose own window is
    /// required to be at least this long.
    /// <para>
    /// <b>It is passed once, on the claim, and the completion no longer
    /// extends it.</b> The window therefore runs from <c>TryClaimAsync</c>
    /// whatever the handler does, which is what puts the claim's window
    /// <em>start</em> before the marker's stamp by construction rather than by
    /// a margin (#168).
    /// </para>
    /// <para>
    /// <b>That start ordering is the only part of this that is by
    /// construction.</b> Neither expiry ordering follows from it. The marker
    /// outliving the claim needs the two windows counted at one rate, and they
    /// are counted by two servers' clocks (#171); the claim outliving the stamp
    /// needs the handler to finish inside this value, which nothing bounds —
    /// §8.5's long-handler residual (#127), whose damage the claim token bounds
    /// rather than removes. A command that runs for an hour spends an hour of
    /// its own replay window, and one that runs for longer than this reaches
    /// §6.3's stamp with nothing left to spend.
    /// </para>
    /// </summary>
    private static readonly TimeSpan Retention = IdempotencyRetention.Window;

    // "null" and not the empty string. IdempotencyEntry carries a Payload the
    // store has to tell apart from the in-progress marker it wrote on the
    // claim, and an empty string is the value an implementation is likeliest to
    // read as absent — which would replay every void-shaped command as
    // ConcurrentRequestException for a day. This is valid JSON and unambiguous.
    private const string NoValue = "null";

    // Result and Result<T> are the whole universe (Appendix D.5), and the two
    // members after this one depend on that. A static field on a generic type
    // has one instance per CLOSED type, so all three are resolved once per
    // (TCommand, TResult) pair rather than once per command, and they run in
    // declaration order.
    private static readonly Type? ValueType = ValueTypeOf();

    private static readonly PropertyInfo? ValueProperty =
        ValueType is null ? null : typeof(TResult).GetProperty(nameof(Result<object>.Value));

    // Result.Success<T>, closed over that value type — and the factory rather
    // than the constructor for a weaker reason than it looks. The constructor
    // is INTERNAL and this behaviour is in the same assembly, so it is
    // reachable, and Success<T> guards nothing the constructor does not: it is
    // `=> new(value, null)`. What it is, is the type's stated construction API
    // (Appendix D.5), and that is the whole of the reason. The state invariant
    // needs neither: IsSuccess is defined as the absence of an error, so
    // success-carrying-an-error is unreachable by any route.
    private static readonly MethodInfo? SuccessOfValue = ValueType is null
        ? null
        : typeof(Result)
            .GetMethod(nameof(Result.Success), 1, [Type.MakeGenericMethodParameter(0)])!
            .MakeGenericMethod(ValueType);

    public async Task<TResult> HandleAsync(TCommand command, NextDelegate<TResult> next, CancellationToken ct)
    {
        // Key shape only — the store owns the service prefix and namespace.
        // The subject segment is not decoration: a key built from the command
        // and the client's value alone is entirely caller-controlled, so caller
        // A can name victim B's key and be handed B's result. Nor is the
        // operation segment free: it is declared on the command rather than
        // derived from the type name, so a rename cannot silently change it.
        string key = $"{Subject()}:{TCommand.OperationName}:{command.CommandId}";

        // The token names THIS attempt, and every write below carries it. A
        // claim that has expired under a long handler cannot be completed or
        // released over its successor's — the store compares before it acts,
        // and a lost claim is a no-op rather than a clobber (#127).
        string? claim = await store.TryClaimAsync(key, Retention, ct);

        if (claim is null)
        {
            IdempotencyEntry? existing = await store.GetAsync(key, ct);

            if (existing is null || existing.InProgress)
                throw new ConcurrentRequestException(command.CommandId);

            return Replay(existing.Payload!);
        }

        // Handed to §6.3, which writes the durable marker under this key inside
        // the transaction and reads it back before anything runs. Set after the
        // claim rather than beside the key, so a command that lost the race and
        // is about to replay never hands a key to a transaction it will not
        // open.
        idempotency.Claim(key);

        TResult result;

        try
        {
            result = await next();
        }
        catch
        {
            // Release for a fault raised INSIDE next(), and nowhere else.
            // §6.3's ExecuteAsync disposes the transaction on the way out,
            // which rolls it back — for every fault this in-process code can
            // tell apart. The one it cannot is the lost commit acknowledgement,
            // where the work IS durable and this line frees the key for it.
            //
            // Releasing is still the right answer, and it is now a decision
            // rather than a default: the retry it admits meets the durable
            // marker §6.3 wrote in that same transaction and is refused with
            // CommandAlreadyCommittedException before a handler runs. Holding
            // instead would buy nothing and cost the ordinary fault its retry
            // for a day — and it would not close the case either, because a
            // held entry expires and the attempt after that finds a free key.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            throw;
        }
        finally
        {
            // The key lives for exactly the dispatch that claimed it. A scope
            // is not promised to serve one command — an endpoint or an
            // integration-event handler may dispatch twice — and a key left
            // standing is captured by the NEXT command's transaction, which
            // either refuses a command nobody protected or writes a marker
            // naming the wrong command's work. Neither is reachable today,
            // which is what makes it worth closing here rather than relying on
            // a premise the next caller falsifies.
            idempotency.Clear();
        }

        if (result.IsFailure)
        {
            // A refusal is rolled back by the same mechanism rather than by
            // §6.3 declining to save: ExecuteAsync disposes an uncommitted
            // transaction. Given that, there is nothing worth replaying, and
            // holding the key would replay a REFUSAL — to the caller who fixed
            // their request and retried under the same key, after the condition
            // that caused it had cleared.
            await store.ReleaseAsync(key, claim, CancellationToken.None);
            return result;
        }

        // No retention here, and the omission is the fix rather than a shorter
        // call. Passing one re-armed the entry at the commit, which is after
        // §6.3 stamped its marker inside the transaction — so the claim
        // outlived the marker by the commit's tail and the marker's own window
        // had to carry a margin for it (#168). The store now keeps what the
        // claim had left, and the outcome stays replayable for the remainder
        // of that window rather than for a fresh one.
        await store.CompleteAsync(key, claim, Capture(result), CancellationToken.None);
        return result;
    }

    // The claim belongs to one subject, bound from the principal and never from
    // the command (§11.4). IsAuthenticated is false for BOTH a message-borne
    // command and an anonymous HTTP request (Appendix D.1), so this segment is
    // shared rather than unique — which is a residual, argued in §8.5, not a
    // detail. It cannot collide with an authenticated subject: the alternative
    // is a Guid rendered "D", and no Guid spells a word.
    private string Subject() => currentUser.IsAuthenticated ? currentUser.Id.ToString() : "system";

    // Only a success is ever stored, and what is stored is its VALUE — never
    // the Result around it, which survives neither direction of a JSON round
    // trip. §8.5's trap callout carries the measurement.
    private static string Capture(TResult result) =>
        ValueType is null
            ? NoValue
            : JsonSerializer.Serialize(ValueProperty!.GetValue(result), ValueType);

    private static TResult Replay(string payload)
    {
        // (TResult)Result.Success() is legal C# under the constraint above and
        // throws InvalidCastException at run time for every TResult that is not
        // exactly Result — the compiler accepts it because Result is TResult's
        // effective base class, and the runtime refuses a base instance where a
        // derived one is required. The guard is what makes it safe, not an
        // optimisation, and removing it fails only at the first replay.
        if (ValueType is null)
            return (TResult)Result.Success();

        object? value = JsonSerializer.Deserialize(payload, ValueType);
        return (TResult)SuccessOfValue!.Invoke(null, [value])!;
    }

    private static Type? ValueTypeOf()
    {
        if (typeof(TResult) == typeof(Result))
            return null;

        if (typeof(TResult).IsGenericType && typeof(TResult).GetGenericTypeDefinition() == typeof(Result<>))
            return typeof(TResult).GetGenericArguments()[0];

        // Unreachable while Result<T> is sealed and Result's constructor is
        // private protected — a third shape could only be declared inside
        // Common.Application. Stated rather than assumed, though what it buys
        // is narrower than it looks: this runs from a static field
        // initialiser, so the CLR wraps it in a TypeInitializationException
        // exactly as it would wrap the IndexOutOfRangeException the obvious
        // body throws. The surface type is the same either way. What changes
        // is the InnerException — a sentence naming the type and the reason,
        // rather than an index that names neither — and moving the check off
        // the static path to get a direct throw would cost it on every
        // command instead of once per closed generic.
        throw new NotSupportedException(
            $"{typeof(TResult).Name} is neither Result nor Result<T>, so no stored outcome " +
            "can be rebuilt for it. A third Result shape is a change to this behaviour.");
    }
}
