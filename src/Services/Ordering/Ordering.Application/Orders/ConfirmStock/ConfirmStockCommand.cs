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
/// <c>OrderStockConfirmedDomainEvent</c> staged onto the outbox by
/// <c>TransactionBehavior</c>. An integration-event handler that mutated the
/// aggregate itself would commit through the inbox filter's
/// <c>SaveChangesAsync</c> and stage nothing, which is a domain event dropped
/// in silence — and silent today only because no projection subscribes to that
/// event yet (§6.6's <c>OrderSummaries</c> is not built).
/// </para>
/// </remarks>
public sealed record ConfirmStockCommand(Guid OrderId) : ICommand<Result>;
