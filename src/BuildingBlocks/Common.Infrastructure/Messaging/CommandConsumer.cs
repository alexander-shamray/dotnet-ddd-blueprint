using Common.Application;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Messaging;

/// <summary>
/// §9.4's mirror of <see cref="IntegrationEventConsumer{TEvent}"/> for commands.
/// They arrive on their own queue, they are not integration events, and they
/// dispatch into the <b>application</b> pipeline — so a command that arrives by
/// message goes through exactly the same behaviours (§6.3) as one that arrives
/// by HTTP.
/// </summary>
/// <remarks>
/// <c>TMessage</c> is a wire contract; <c>TCommand</c> is the application
/// command it maps to. <c>ICommandHandler&lt;,&gt;</c> appears twice in §9.4's
/// delivery table deliberately: a command is the same application operation
/// whether a user submitted it or a saga sent it, and it must not grow a second
/// implementation because of how it arrived.
/// </remarks>
public sealed class CommandConsumer<TMessage, TCommand>(
    IDispatcher dispatcher,
    ICommandMessageMapper<TMessage, TCommand> mapper,
    MessagingMetrics metrics,
    ILogger<CommandConsumer<TMessage, TCommand>> log)
    : IConsumer<TMessage>
    where TMessage : class
    where TCommand : ICommand<Result>
{
    // Compiled once per closed consumer. CA1848 (ADR-019) again, and required
    // for the same reason as §13.3's LoggingBehavior: a consumer runs on every
    // message that arrives.
    private static readonly Action<ILogger, string, string, string, Guid?, Exception?> DomainRejected =
        LoggerMessage.Define<string, string, string, Guid?>(
            LogLevel.Warning,
            new EventId(1, nameof(DomainRejected)),
            "{MessageType} rejected by the domain: {ErrorCode} {ErrorDescription}. " +
            "CorrelationId {CorrelationId}.");

    public async Task Consume(ConsumeContext<TMessage> context)
    {
        // Mapping is explicit: the wire type is a contract, the command is an
        // application type, and CancelOrder.Reason is a string that has to be
        // parsed back into CancellationReason (§9.6). A ContractMappingException
        // from here is excluded from retry by the endpoint's policy (§9.8) — a
        // malformed contract does not parse itself on the fourth attempt.
        TCommand command = mapper.Map(context.Message);

        Result result = await dispatcher.SendAsync(command, context.CancellationToken);

        // A domain rejection is an answer, not a delivery failure. The message
        // was received, understood and refused, and no redelivery changes that
        // — so it is acked, counted and logged rather than thrown (§9.8).
        //
        // This is the last place that can tell a rejection from a fault. An
        // exception from the dispatcher propagates and MassTransit retries it,
        // which is correct: that is a fault. Everything below is the other case,
        // and keeping it out of the error queue is what lets §13.6's depth
        // alert stay at a threshold nobody has to interpret.
        //
        // Unavailable is not that case, and reading `IsFailure` as "the domain
        // refused" swept it in. §9.8's own rule is that retry is for faults time
        // might fix, and ErrorType.Unavailable is that fault arriving as a
        // returned value rather than a thrown one — a downstream dependency that
        // is down, which §10.5 answers over HTTP with a 503 so the caller tries
        // again. There is no caller here to do that: the saga that sent it has
        // moved on (§9.7), so acking is the end of the command, and the inbox
        // row committed on the way out (§9.5) means even a manual replay of the
        // same message is dropped as already handled.
        if (result.IsFailure && result.Error.Type == ErrorType.Unavailable)
            throw new UnavailableResultException(result.Error);

        if (result.IsFailure)
        {
            metrics.Rejected(typeof(TMessage).Name, result.Error.Code);

            DomainRejected(
                log,
                typeof(TMessage).Name,
                result.Error.Code,
                result.Error.Description,
                context.CorrelationId,
                null);
        }
    }
}
