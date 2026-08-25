namespace Common.Contracts.Payments.V1;

/// <summary>
/// Authorise a payment for an order (§3.2's Accepts column), sent by the saga
/// to <c>payments-commands</c> (§9.6).
/// </summary>
/// <remarks>
/// The currency travels with the amount, and §9.6 says why in one line: a bare
/// decimal is a charge waiting to be made in the wrong denomination.
/// <para>
/// <b>It carries no subject, and the omission is the control</b> (ADR-028).
/// This command decides whose payment instrument is charged, and a
/// <c>CustomerId</c> here would be that decision taken from a field the
/// receiver cannot check — the broker carries no principal (§11.4), so nothing
/// on the receiving side could tell a real subject from a chosen one. Payments
/// resolves the payer from its own record of the order instead, built from the
/// <c>OrderPlaced</c> it consumes (§3.2).
/// </para>
/// <para>
/// <b><c>Amount</c> and <c>Currency</c> stay, and the difference is not
/// checkability.</b> Payments holds the order, so it could compare a supplied
/// <c>CustomerId</c> against its record exactly as it compares these two —
/// which is why the checkability argument this paragraph used to make was
/// wrong, and wrong because of the record this very decision introduces.
/// </para>
/// <para>
/// The line that does hold is <b>instruction versus authority</b>. These two
/// say <em>what to do</em>, the sender decides them, and the receiver may
/// refuse a mismatch as a consistency check. A subject says <em>on whose
/// behalf</em>, and that is the receiver's own to derive: transporting it
/// creates a second source for a decision that must have exactly one. A check
/// that exists is not a check that is performed, and the field that is not
/// there cannot be the one a later code path reads instead of the record.
/// </para>
/// </remarks>
public sealed record AuthorisePayment(Guid OrderId, decimal Amount, string Currency);
