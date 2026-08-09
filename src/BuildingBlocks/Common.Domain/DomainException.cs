namespace Common.Domain;

/// <summary>
/// Signals a broken invariant — a bug, not user input (§5.7). Malformed input
/// is rejected by the application layer's validation before any domain method
/// runs, so a guard in a factory or an operation that fires anyway means a
/// handler bypassed the always-valid boundary, and the exception is the loud
/// version of that fact.
/// </summary>
public class DomainException(string message) : Exception(message);
