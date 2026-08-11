using Common.Application;
using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §9.4's mirror of the event consumer, for commands — and the type behind
/// §9.8's most argued decision: a domain rejection is acked, counted and
/// logged rather than thrown, so the error queue holds only faults and its
/// depth alert can stay at a threshold nobody has to interpret.
/// </summary>
/// <remarks>
/// Constructed directly rather than driven through the harness, unlike
/// <c>IntegrationEventConsumerTests</c>. What is under test is the branch after
/// the dispatcher returns, and reaching it through a bus would need a real
/// application pipeline — which means a transaction, a unit of work and a
/// database, none of which this behaviour depends on. Catalog's Accepts column
/// is empty by §3.2, so there is no real command message to send either.
/// </remarks>
public class CommandConsumerTests
{
    public sealed record ProbeMessage(Guid OrderId);

    public sealed record ProbeCommand(Guid OrderId) : ICommand<Result>;

    private static readonly Error Refused =
        new("probe.refused", "the domain refused this", ErrorType.Rule);

    private static readonly Error Unreachable =
        new("probe.unreachable", "a dependency is down", ErrorType.Unavailable);

    private static CommandConsumer<ProbeMessage, ProbeCommand> Build(
        Result outcome,
        MessagingMetrics metrics,
        ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>? log = null)
    {
        IDispatcher dispatcher = Substitute.For<IDispatcher>();
        dispatcher
            .SendAsync(Arg.Any<ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(outcome));

        ICommandMessageMapper<ProbeMessage, ProbeCommand> mapper =
            Substitute.For<ICommandMessageMapper<ProbeMessage, ProbeCommand>>();
        mapper.Map(Arg.Any<ProbeMessage>()).Returns(c => new ProbeCommand(c.Arg<ProbeMessage>().OrderId));

        return new CommandConsumer<ProbeMessage, ProbeCommand>(
            dispatcher,
            mapper,
            metrics,
            log ?? Substitute.For<ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>>());
    }

    private static ConsumeContext<ProbeMessage> Context(ProbeMessage message)
    {
        ConsumeContext<ProbeMessage> context = Substitute.For<ConsumeContext<ProbeMessage>>();
        context.Message.Returns(message);
        context.CancellationToken.Returns(TestContext.Current.CancellationToken);
        context.CorrelationId.Returns(message.OrderId);

        return context;
    }

    private static MessagingMetrics Metrics() =>
        new(new TestMeterFactory());

    [Fact]
    public async Task A_message_borne_command_goes_through_the_application_dispatcher()
    {
        // The whole reason this type exists rather than a second handler: a
        // command is the same application operation whether a user submitted it
        // or a saga sent it, so it goes through the §6.3 behaviours either way.
        IDispatcher dispatcher = Substitute.For<IDispatcher>();
        dispatcher
            .SendAsync(Arg.Any<ICommand<Result>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result.Success()));

        ICommandMessageMapper<ProbeMessage, ProbeCommand> mapper =
            Substitute.For<ICommandMessageMapper<ProbeMessage, ProbeCommand>>();
        var orderId = Guid.CreateVersion7();
        mapper.Map(Arg.Any<ProbeMessage>()).Returns(new ProbeCommand(orderId));

        CommandConsumer<ProbeMessage, ProbeCommand> consumer = new(
            dispatcher,
            mapper,
            Metrics(),
            Substitute.For<ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>>());

        await consumer.Consume(Context(new ProbeMessage(orderId)));

        // The mapped command, not the wire message — the mapping is explicit
        // because the two are not the same kind of thing (§9.4).
        await dispatcher.Received(1).SendAsync(
            Arg.Is<ICommand<Result>>(c => ((ProbeCommand)c).OrderId == orderId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_domain_rejection_is_acked_rather_than_thrown()
    {
        // §9.8's table, third row. Retrying is wrong — a shipped order is still
        // shipped on the fifth attempt — and so is throwing to skip retry: that
        // reaches the error queue once instead of after a minute, and a routine
        // outcome then sits in a queue whose depth alert pages a human.
        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Refused), Metrics());

        await Should.NotThrowAsync(() => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));
    }

    [Fact]
    public async Task A_domain_rejection_is_counted_with_the_error_code_as_its_tag()
    {
        // "An outcome with a metric and a log line is not silent" is the whole
        // answer to the objection that acking makes a refused command disappear
        // (§9.8) — so the counter is what makes the ack defensible, and its
        // absence would be the swallow the objection describes.
        using RecordedMeasurements measurements = new("Commerce.Messaging");

        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Refused), Metrics());

        await consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7())));

        RecordedMeasurements.Measurement rejection =
            measurements.For("command.domain_rejected").ShouldHaveSingleItem();

        rejection.Value.ShouldBe(1);
        rejection.Tag("message").ShouldBe(nameof(ProbeMessage));

        // An Error.Code and nothing else. A cancellation reason reads like
        // something worth counting here and describes the opposite event — a
        // command the domain accepted — and both are lowercase snake_case
        // strings on a counter tagged `error`, which is what makes the mistake
        // invisible in a dashboard (§9.8).
        rejection.Tag("error").ShouldBe(Refused.Code);
    }

    [Fact]
    public async Task A_successful_command_records_no_rejection()
    {
        // The other direction of the same counter: a series that also counts
        // successes measures traffic rather than refusals, and the dashboard
        // §9.8 describes would read the two as one.
        using RecordedMeasurements measurements = new("Commerce.Messaging");

        CommandConsumer<ProbeMessage, ProbeCommand> consumer = Build(Result.Success(), Metrics());

        await consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7())));

        measurements.For("command.domain_rejected").ShouldBeEmpty();
    }

    [Fact]
    public async Task An_unavailable_result_is_thrown_so_the_retry_policy_can_see_it()
    {
        // The half `IsFailure` swept in. §9.8's rule is that retry is for faults
        // time might fix, and ErrorType.Unavailable is exactly that — a
        // downstream dependency that is down — arriving as a returned value
        // rather than a thrown one. §10.5 answers it over HTTP with a 503 and
        // the caller retries; this path has no caller, so acking is the end of
        // the command and the inbox row (§9.5) makes even a manual replay a
        // no-op.
        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Unreachable), Metrics());

        UnavailableResultException thrown = await Should.ThrowAsync<UnavailableResultException>(
            () => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));

        // The code travels with it, because the fault MassTransit logs is the
        // only record this outcome gets — there is no counter for it, by
        // design: a transient failure that clears on retry is not an event, and
        // one that does not reaches the error queue, where §13.6 already alerts.
        thrown.Error?.Code.ShouldBe(Unreachable.Code);
    }

    [Fact]
    public async Task An_unavailable_result_is_not_counted_as_a_domain_rejection()
    {
        // The other direction, and the one that would go quiet if the throw were
        // added above the counter rather than instead of it. `command.domain_rejected`
        // belongs on a dashboard rather than a pager (§9.8) precisely because
        // every value in it is an answer the domain gave; a downstream outage
        // counted there reads as a business signal and moves with load.
        using RecordedMeasurements measurements = new("Commerce.Messaging");

        CommandConsumer<ProbeMessage, ProbeCommand> consumer =
            Build(Result.Failure(Unreachable), Metrics());

        await Should.ThrowAsync<UnavailableResultException>(
            () => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));

        measurements.For("command.domain_rejected").ShouldBeEmpty();
    }

    [Fact]
    public async Task A_mapping_failure_propagates_so_the_endpoint_policy_can_see_it()
    {
        // ContractMappingException exists to be excluded from retry (§9.8), and
        // it can only be excluded if it leaves the consumer. Swallowing it here
        // would ack a message nobody handled — the loss the inbox makes
        // permanent.
        IDispatcher dispatcher = Substitute.For<IDispatcher>();

        ICommandMessageMapper<ProbeMessage, ProbeCommand> mapper =
            Substitute.For<ICommandMessageMapper<ProbeMessage, ProbeCommand>>();
        mapper
            .Map(Arg.Any<ProbeMessage>())
            .Returns(_ => throw new ContractMappingException("unknown reason code 'wat'"));

        CommandConsumer<ProbeMessage, ProbeCommand> consumer = new(
            dispatcher,
            mapper,
            Metrics(),
            Substitute.For<ILogger<CommandConsumer<ProbeMessage, ProbeCommand>>>());

        await Should.ThrowAsync<ContractMappingException>(
            () => consumer.Consume(Context(new ProbeMessage(Guid.CreateVersion7()))));

        await dispatcher.DidNotReceive().SendAsync(
            Arg.Any<ICommand<Result>>(),
            Arg.Any<CancellationToken>());
    }
}
