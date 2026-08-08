using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Catalog.Api.Tests;

/// <summary>
/// The marker the retry test throws on its first attempt. A real transient
/// fault is a SqlException with one of a fixed set of numbers, and those are
/// not constructible; a strategy taught to retry this marker exercises the
/// same path without reflection over provider internals.
/// </summary>
public sealed class FakeTransientException : Exception;

/// <summary>
/// The production strategy plus one retriable exception type. Everything the
/// test proves — the delegate re-runs, the first attempt rolls back, one
/// commit — is the base class's behaviour, not this subclass's.
/// </summary>
public sealed class MarkerRetryingStrategy(ExecutionStrategyDependencies dependencies)
    : SqlServerRetryingExecutionStrategy(dependencies)
{
    protected override bool ShouldRetryOn(Exception exception) =>
        exception is FakeTransientException || base.ShouldRetryOn(exception);
}
