using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Infrastructure.Outbox;

namespace Ordering.TestSupport.Outbox;

/// <summary>
/// Ordinary factories over <see cref="OutboxMessage"/>, staged through the
/// <b>real</b> <see cref="MessageTypeMap"/> and <see cref="OutboxJson"/>
/// resolved from the fixture's provider (§12.4). Doubles for either would let
/// a test stage a row the running host cannot read back, which is the one
/// thing these builders exist to prove does not happen.
/// </summary>
public static class OutboxRows
{
    private static readonly DateTimeOffset Raised = new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    /// <summary>A row whose registered handler always throws.</summary>
    public static OutboxMessage Poison(ServiceFixture fixture) =>
        Local(new AlwaysThrows { OccurredAt = Raised }, fixture);

    /// <summary>A row whose registered handler does nothing, successfully.</summary>
    public static OutboxMessage Healthy(ServiceFixture fixture) =>
        Local(new NoOpEvent { OccurredAt = Raised }, fixture);

    /// <summary>
    /// A healthy row carrying an arbitrarily long payload, for the assertion
    /// that <c>Payload</c> is genuinely <c>nvarchar(max)</c> and not §7.2's
    /// 400-character string convention wearing that column type.
    /// </summary>
    public static OutboxMessage Verbose(ServiceFixture fixture, string note) =>
        Local(new NoOpEvent { OccurredAt = Raised, Note = note }, fixture);

    /// <summary>A row whose handler waits on <see cref="DeliveryGate"/>.</summary>
    public static OutboxMessage Blocking(ServiceFixture fixture) =>
        Local(new BlocksUntilReleased { OccurredAt = Raised }, fixture);

    /// <summary>
    /// A Broker-lane row carrying a real contract, so the publish half of
    /// <c>DeliverAsync</c> is exercised against the running broker rather than
    /// inferred from the staging tests.
    /// </summary>
    /// <remarks>
    /// <b>It arrived with PR-21, which is when it could.</b> Staging this lane
    /// needs a type <c>Common.Contracts</c> publishes on this service's behalf
    /// and the §9.3 allow-list mapping something to it — both true only once
    /// the saga gave Ordering a reason to publish <c>OrderPlaced</c>. Until
    /// then the three tests that use it would each have asserted against a row
    /// no code here could produce.
    /// <para>
    /// <c>OrderPlaced</c> rather than the other two Ordering now publishes: it
    /// is the one §9.6's saga starts on, so a payload this builder cannot
    /// round-trip is a workflow that never begins.
    /// </para>
    /// </remarks>
    public static OutboxMessage Broker(ServiceFixture fixture, Guid orderId) =>
        OutboxMessage.Stage(
            new OrderPlaced
            {
                MessageId = Guid.CreateVersion7(),
                CorrelationId = orderId,
                OccurredAt = Raised,
                OrderId = orderId,
                CustomerId = Guid.CreateVersion7(),
                TotalAmount = 129.98m,
                Currency = "EUR",
                Lines = [new PlacedLine(Guid.CreateVersion7(), 2, 64.99m)]
            },
            OutboxLane.Broker,
            orderId,
            fixture.MessageTypes,
            fixture.OutboxJson);

    /// <summary>A row for an event type with no handler at all.</summary>
    public static OutboxMessage Unhandled(ServiceFixture fixture) =>
        Local(new UnhandledEvent { OccurredAt = Raised }, fixture);

    private static OutboxMessage Local(object message, ServiceFixture fixture) =>
        OutboxMessage.Stage(
            message,
            OutboxLane.Local,
            Guid.CreateVersion7(),
            fixture.MessageTypes,
            fixture.OutboxJson);
}
