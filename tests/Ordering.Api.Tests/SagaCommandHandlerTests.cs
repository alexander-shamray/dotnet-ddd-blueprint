using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Ordering.Application.Orders;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.ConfirmStock;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Domain.Orders;
using Ordering.TestSupport;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// The refusal branches of the three saga-only handlers, which decide whether a
/// command is retried or acknowledged.
/// </summary>
/// <remarks>
/// <b>Only the success paths were covered, and the distinction these tests pin
/// is the one with consequences.</b> `CommandConsumer` reads `ErrorType`
/// (§9.8): `Unavailable` becomes an `UnavailableResultException` the endpoint's
/// backoff retries, and everything else is acked, counted and gone. So
/// collapsing `StockNotConfirmed` into a `Rule` error would leave a **paid
/// order permanently unconfirmed** — and every suite would stay green, because
/// the endpoint tests only ever drive the happy path. Copilot found the gap.
/// <para>
/// Dispatched rather than sent, because the branch under test is the handler's
/// return value and the endpoint converts it into a retry that hides it. Homed
/// here rather than in <c>Ordering.Application.Tests</c> because these handlers
/// load an aggregate, which needs the fixture's database (§12.1's note).
/// </para>
/// </remarks>
[Collection(nameof(IntegrationCollection))]
public sealed class SagaCommandHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    private static readonly Guid Customer = Guid.Parse("77777777-7777-7777-7777-777777777777");

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Confirming_an_order_still_awaiting_stock_is_retryable()
    {
        // The ordering race: Ordering learns of the reservation and of the
        // authorisation on two receive endpoints with nothing sequencing them.
        // This must be Unavailable — a Rule error here is acked, and the order
        // stays unconfirmed for good with the money taken.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        Result result = await DispatchAsync(
            new ConfirmOrderCommand(orderId, PaymentReference.Of("psp-race-1")));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(OrderErrors.StockNotConfirmed);
        result.Error.Type.ShouldBe(
            ErrorType.Unavailable,
            "CommandConsumer retries only this type — a Rule error would ack a paid order's " +
            "confirmation permanently (§9.8)");

        (await StatusAsync(orderId)).ShouldBe("AwaitingStock", "a refusal mutates nothing");
    }

    [Fact]
    public async Task Confirming_an_order_that_has_moved_on_is_a_rejection()
    {
        // The other side of the same branch. A cancelled order will refuse this
        // command on the fifth attempt exactly as on the first, so retrying it
        // is a minute of backoff and an error queue entry §13.6 pages on.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        await DispatchAsync(new ConfirmStockCommand(orderId));
        await DispatchAsync(new ConfirmOrderCommand(orderId, PaymentReference.Of("psp-first")));

        Result result = await DispatchAsync(
            new ConfirmOrderCommand(orderId, PaymentReference.Of("psp-second")));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(OrderErrors.NotAwaitingPayment);
        result.Error.Type.ShouldBe(ErrorType.Rule, "no retry makes a confirmed order confirmable again");
    }

    [Fact]
    public async Task Marking_an_unconfirmed_order_shipped_is_retryable()
    {
        // Shipping cannot despatch what was never confirmed, so a despatch
        // arriving first is evidence the confirmation exists and has not
        // landed — which time fixes.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        Result result = await DispatchAsync(
            new MarkOrderShippedCommand(orderId, TrackingNumber.Of("TRACK-EARLY")));

        result.Error.ShouldBe(OrderErrors.NotConfirmed);
        result.Error.Type.ShouldBe(ErrorType.Unavailable);
    }

    [Fact]
    public async Task Marking_a_cancelled_order_shipped_is_a_rejection()
    {
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        await DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, CommandOrigin.System));

        Result result = await DispatchAsync(
            new MarkOrderShippedCommand(orderId, TrackingNumber.Of("TRACK-LATE")));

        result.Error.ShouldBe(OrderErrors.NotShippable);
        result.Error.Type.ShouldBe(
            ErrorType.Rule,
            "a cancelled order refuses identically on every attempt — §9.8 keeps that out of the " +
            "error queue, where depth greater than zero pages a human");
    }

    [Fact]
    public async Task A_reservation_for_an_order_that_moved_on_is_rejected_and_changes_nothing()
    {
        // §9.6's stock-timeout residual, from the aggregate's side: the saga
        // cancelled and finalised, Inventory's reservation arrives afterwards,
        // and this is the refusal that leaves the stock held. The decision log
        // names it as owed; the behaviour it names is this one, and nothing
        // pinned it.
        Guid orderId = await fixture.SeedOrderAsync(Customer);
        await DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.StockTimeout, CommandOrigin.System));

        Result result = await DispatchAsync(new ConfirmStockCommand(orderId));

        result.Error.ShouldBe(OrderErrors.NotAwaitingStock);
        result.Error.Type.ShouldBe(
            ErrorType.Rule,
            "no attempt from here releases the reservation, so retrying is a queue entry and not a fix");

        (await StatusAsync(orderId)).ShouldBe("Cancelled", "the refusal must not move a cancelled order");
    }

    [Fact]
    public async Task A_command_for_an_order_that_does_not_exist_is_not_found()
    {
        // All three handlers share this first branch, and it is the one a
        // misrouted correlation id reaches.
        var missing = Guid.CreateVersion7();

        (await DispatchAsync(new ConfirmStockCommand(missing))).Error.ShouldBe(OrderErrors.NotFound);
        (await DispatchAsync(new ConfirmOrderCommand(missing, PaymentReference.Of("psp-x"))))
            .Error
            .ShouldBe(OrderErrors.NotFound);
        (await DispatchAsync(new MarkOrderShippedCommand(missing, TrackingNumber.Of("TRACK-X"))))
            .Error
            .ShouldBe(OrderErrors.NotFound);
    }

    [Theory]
    [InlineData(CommandOrigin.System, CancelOrigins.Workflow)]
    [InlineData(CommandOrigin.User, CancelOrigins.User)]
    public async Task The_commands_origin_decides_the_published_events_origin(
        CommandOrigin initiatedBy,
        string expected)
    {
        // **The one translation #123 rests on, and nothing exercised it.**
        // OrderTests proves the aggregate carries whatever origin it is handed
        // and the mapper suite proves the enum reaches the wire, but the switch
        // BETWEEN them was covered by neither — so inverting System and User in
        // the handler would leave every one of those tests green while tagging
        // real saga cancellations as user requests and customer cancellations as
        // this workflow's own echo. §9.6 then discards exactly the arrivals it
        // exists to fault. Copilot found the gap.
        //
        // Read off the OUTBOX row rather than the domain event: that is the
        // payload a consumer sees, so it covers the mapper and the handler in
        // one assertion and cannot pass on a translation that stops halfway.
        Guid orderId = await fixture.SeedOrderAsync(Customer);

        (await DispatchAsync(
            new CancelOrderCommand(orderId, CancellationReason.CustomerRequest, initiatedBy)))
            .IsSuccess.ShouldBeTrue();

        OutboxMessage row = (await fixture.OutboxAsync())
            .ShouldHaveSingleItem();

        row.Payload.ShouldContain(
            $"\"Origin\":\"{expected}\"",
            Case.Sensitive,
            "the origin a consumer reads is the one this handler chose");
    }

    private async Task<Result> DispatchAsync(ICommand<Result> command)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();

        return await scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, TestContext.Current.CancellationToken);
    }

    private async Task<string> StatusAsync(Guid orderId) =>
        await fixture.ScalarAsync<string>(
            "SELECT Value = Status FROM ordering.Orders WHERE Id = {0}",
            orderId);
}
