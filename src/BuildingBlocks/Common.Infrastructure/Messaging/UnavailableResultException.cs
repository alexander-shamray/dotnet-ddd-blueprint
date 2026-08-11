using Common.Application;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// A handler returned <see cref="ErrorType.Unavailable"/> for a command that
/// arrived by message. Thrown by <see cref="CommandConsumer{TMessage, TCommand}"/>
/// so §9.8's retry policy sees it, because on this path nothing else will.
/// </summary>
/// <remarks>
/// <b>The same <see cref="Result"/> means different things on the two paths, and
/// that is the whole reason this type exists.</b> Over HTTP,
/// <see cref="ErrorType.Unavailable"/> becomes a 503 (§10.5) and the caller
/// retries — the retry is real, it is just somebody else's. A message-borne
/// command has no such caller: the sender is a saga that has already moved on
/// (§9.7), so acking would be the last thing that ever happened to it, and the
/// inbox row committed on the way out (§9.5) would drop the redelivery that
/// might have succeeded.
/// <para>
/// The three parameterless-through-inner constructors are CA1032's, as on
/// <see cref="ContractMappingException"/>; the fourth is the one the consumer
/// actually calls.
/// </para>
/// </remarks>
public sealed class UnavailableResultException : Exception
{
    public UnavailableResultException()
    {
    }

    public UnavailableResultException(string message)
        : base(message)
    {
    }

    public UnavailableResultException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UnavailableResultException(Error error)
        : base($"A handler returned {error.Code}: {error.Description}. " +
            "ErrorType.Unavailable is a transient condition, so the message is faulted " +
            "rather than acked and §9.8's policy retries it.") =>
        Error = error;

    /// <summary>The failure the handler returned, for a log that wants the code.</summary>
    public Error? Error { get; }
}
