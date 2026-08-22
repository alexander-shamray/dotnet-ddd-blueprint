namespace Common.Application;

/// <summary>
/// Opts a command into <see cref="IdempotencyBehavior{TCommand,TResult}"/>. Not
/// an empty marker: the behaviour reads both members to build its key, so the
/// interface has to carry them.
/// </summary>
/// <remarks>
/// The behaviour is constrained to this, which means a command that does not
/// declare it is simply never protected — no error, no warning, and a retry
/// creates a second order. Opting in is a decision; forgetting to is not meant
/// to look like one, which is why a reflection gate reads intent off the shape
/// of the command rather than trusting the author to have opted in (§8.5).
/// <para>
/// <b>An idempotent command's endpoint must require authentication.</b>
/// <see cref="ICurrentUser.IsAuthenticated"/> is false for an anonymous HTTP
/// request and for a message-borne command alike, so both claim under one
/// shared subject — and on an anonymous endpoint the cross-caller collision the
/// subject segment exists to close is fully reachable inside the fix for it.
/// §8.5 states the rule, and
/// <c>Every_idempotent_command_reaches_this_service_through_an_authenticated_endpoint</c>
/// asserts it — one per service, in that service's <c>AuthorizationPolicyTests</c>.
/// <para>
/// <b>"A test asserts it" is what this said, and it is a claim about a
/// repository rather than about the rule.</b> §4.5's scaffold classes
/// <c>AuthorizationPolicyTests</c> as a slice file and drops it: a service
/// with no endpoint has no policy to enumerate, and that suite's own
/// anti-vacuity floor would fail on a tree that is correct. So a rendered
/// service carries this rule with nothing enforcing it, and the sentence
/// above read as though it could not.
/// </para>
/// <para>
/// <b>What makes that self-clearing rather than a TODO</b> is the floor in
/// the scaffolded <c>IdempotencyOptInTests</c>: a rendered service asserts
/// that it opts <i>no</i> command into idempotency, so the day it opts one in
/// that test fails — and its message names this gate as the other thing owed.
/// The prompt therefore arrives at the moment the rule first has a subject,
/// which is the only moment it could be acted on.
/// </para>
/// </remarks>
public interface IIdempotentCommand
{
    /// <summary>
    /// The operation's stable identity, and the middle segment of the key.
    /// </summary>
    /// <remarks>
    /// <b>Declared rather than derived, because a refactor must not be able to
    /// change it silently.</b> §8.5 built this segment from
    /// <c>typeof(TCommand).Name</c>, which a rename changes — and a rolling
    /// deployment then serves both spellings at once, so a client retrying one
    /// <see cref="CommandId"/> is protected by neither claim and writes twice.
    /// The exposure outlasts the rollout by the retention rather than by the
    /// rollout, because an entry written under the old name stays claimable for
    /// a further 24 hours.
    /// <para>
    /// <c>FullName</c> is the obvious alternative and is strictly worse: it
    /// addresses a collision between two same-named commands in different
    /// namespaces — a real but different problem — while binding the namespace
    /// into the key as well, so moving a command between folders breaks it too.
    /// A <c>static abstract</c> member is what C# 14 makes cheapest: the
    /// compiler refuses a command that does not supply one, so the decision
    /// cannot be skipped, and a rename of the type leaves the string alone.
    /// </para>
    /// <para>
    /// <b>Give it a value the domain would recognise, never the type's name.</b>
    /// A value copied from the CLR name reintroduces the coupling by
    /// convention, and the next reader has no way to tell it was meant to be
    /// stable. Changing one is a migration, on the same terms a rename was.
    /// </para>
    /// <para>
    /// <b>And it must be unique within the service, which the advice above
    /// makes easier to get wrong.</b> A domain-recognisable string is exactly
    /// the kind two commands plausibly share, and this is a segment of a Redis
    /// key whose other two are the subject and the caller's
    /// <see cref="CommandId"/> — so two commands sharing one collapse into a
    /// single keyspace, and a caller reusing a <c>CommandId</c> across them is
    /// served the first command's stored payload, deserialised into the
    /// second's result type. A gate per service asserts the names are
    /// distinct; nothing in the compiler can.
    /// </para>
    /// </remarks>
    static abstract string OperationName { get; }

    /// <summary>
    /// The client-generated identity of this attempt. Two requests carrying one
    /// value are one operation.
    /// </summary>
    /// <remarks>
    /// A field on the command rather than an <c>Idempotency-Key</c> header, and
    /// the reason is the dependency rule rather than taste: the behaviour runs
    /// in this assembly, which knows nothing about HTTP (§4.2), so it cannot
    /// read a header and the value has to be on the command by the time the
    /// pipeline sees it. An endpoint may bind the header into this at the
    /// boundary — the only layer permitted to know either name.
    /// </remarks>
    Guid CommandId { get; }
}
