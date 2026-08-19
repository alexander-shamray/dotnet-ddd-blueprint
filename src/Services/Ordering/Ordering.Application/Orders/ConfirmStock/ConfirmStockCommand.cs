using Common.Application;

namespace Ordering.Application.Orders.ConfirmStock;

/// <summary>
/// Record that Inventory has held stock for an order — the transition
/// <see cref="Ordering.Domain.Orders.Order.ConfirmStock"/> exists for, and the
/// one §5.4 documents with no caller until now.
/// </summary>
/// <remarks>
/// <b>This is not a contract, and the distinction is the whole design.</b>
/// §3.2's Accepts column for Ordering is closed at four commands and §9.6's
/// saga sends exactly those four; adding a fifth to the wire would change both
/// chapters and give Inventory's reservation a second way to reach this
/// service. It does not need one: <c>StockReserved</c> is already in §3.2's
/// Consumes column for Ordering, so what was missing was a consumer, not a
/// contract. <c>StockReservedHandler</c> is that consumer and dispatches this
/// command, which lives and dies inside <c>Ordering.Application</c>.
/// <para>
/// Dispatching rather than writing through the repository directly is what
/// puts the work inside §6.3's pipeline — one transaction, and
/// <c>OrderStockConfirmedDomainEvent</c> on the path
/// <c>TransactionBehavior</c> stages from. An integration-event handler that
/// mutated the aggregate itself would commit through the inbox filter's
/// <c>SaveChangesAsync</c>, off that path entirely.
/// <para>
/// <b>Today that path stages nothing, and the difference is latent rather
/// than observable.</b> <c>DomainEventDispatcher</c> writes a Local row only
/// for an event with a registered projection handler, Ordering registers
/// none, and this event is not on §9.3's Broker allow-list either — so it is
/// collected and cleared with no row of either lane. What dispatching buys is
/// that the row appears the day §6.6's <c>OrderSummaries</c> is built,
/// against a handler that would otherwise have to be found and moved. Copilot
/// caught this claim asserting the staging as a present fact.
/// </para>
/// </para>
/// </remarks>
public sealed record ConfirmStockCommand(Guid OrderId) : ICommand<Result>;
