namespace Common.Contracts.Payments.V1;

/// <summary>
/// Authorise a payment for an order (§3.2's Accepts column), sent by the saga
/// to <c>payments-commands</c> (§9.6).
/// </summary>
/// <remarks>
/// The currency travels with the amount, and §9.6 says why in one line: a bare
/// decimal is a charge waiting to be made in the wrong denomination.
/// </remarks>
public sealed record AuthorisePayment(Guid OrderId, Guid CustomerId, decimal Amount, string Currency);
