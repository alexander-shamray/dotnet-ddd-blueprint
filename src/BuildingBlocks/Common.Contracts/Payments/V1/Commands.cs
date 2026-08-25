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
/// <b><c>Amount</c> and <c>Currency</c> stay, and the difference is whether
/// the receiver can disagree.</b> Payments holds the order, so it can compare
/// these two against it and refuse a mismatch; it holds nothing that would
/// contradict a subject. A field the receiver can check is a claim, and a
/// field it cannot check is an assertion — this contract carries claims.
/// </para>
/// </remarks>
public sealed record AuthorisePayment(Guid OrderId, decimal Amount, string Currency);
