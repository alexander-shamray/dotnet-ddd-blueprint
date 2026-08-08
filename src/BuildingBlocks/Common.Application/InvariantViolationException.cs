namespace Common.Application;

/// <summary>
/// Thrown when a command modifies more than one aggregate root (§2.3,
/// principle 3). Raised by <c>TransactionBehavior</c> at the transaction
/// boundary — the violation is not structural, so no architecture test can
/// catch it; the first execution that does it fails instead, with the command
/// name and the count in the message (§6.3).
/// </summary>
public sealed class InvariantViolationException(string message) : Exception(message);
