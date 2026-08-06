using FluentValidation;
using Microsoft.Extensions.Time.Testing;

namespace Common.Application.Tests;

/// <summary>
/// The anonymous sample requests the dispatcher and behaviour suites share,
/// standing in for §6.4's <c>PlaceOrderCommand</c> and §6.5's query. They carry
/// no domain: what is under test is the pipeline, and a request with meaning
/// invites assertions about the meaning instead.
/// </summary>
public sealed record Ping(string Message) : ICommand<string>;

public sealed class PingHandler : ICommandHandler<Ping, string>
{
    public Task<string> HandleAsync(Ping command, CancellationToken ct) =>
        Task.FromResult($"pong:{command.Message}");
}

public sealed record Ask(string Question) : IQuery<string>;

public sealed class AskHandler : IQueryHandler<Ask, string>
{
    public Task<string> HandleAsync(Ask query, CancellationToken ct) =>
        Task.FromResult($"answer:{query.Question}");
}

/// <summary>A command nothing handles — the §6.2 trap, made deliberate.</summary>
public sealed record Unhandled : ICommand<string>;

/// <summary>A command whose handler throws, for the failure arm of §13.3.</summary>
public sealed record Boom : ICommand<string>;

public sealed class BoomHandler : ICommandHandler<Boom, string>
{
    public Task<string> HandleAsync(Boom command, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}

/// <summary>
/// Advances the clock inside the handler, so the duration §13.3 records is a
/// number the test chose rather than however long the machine took.
/// </summary>
public sealed record Tick : ICommand<string>;

public sealed class TickHandler(FakeTimeProvider clock) : ICommandHandler<Tick, string>
{
    public Task<string> HandleAsync(Tick command, CancellationToken ct)
    {
        clock.Advance(TimeSpan.FromMilliseconds(250));
        return Task.FromResult("ticked");
    }
}

/// <summary>
/// A command the domain refuses. §13.3 counts it as an <c>ok</c> outcome: a
/// rejection is a working system saying no, and tagging it <c>error</c> makes
/// the one number that should mean "something is broken" track customers.
/// </summary>
public sealed record Reject : ICommand<Result>;

public sealed class RejectHandler : ICommandHandler<Reject, Result>
{
    public Task<Result> HandleAsync(Reject command, CancellationToken ct) =>
        Task.FromResult(Result.Failure(Error.Rule("test.rejected", "The domain said no.")));
}

public sealed class PingValidator : AbstractValidator<Ping>
{
    public PingValidator() => RuleFor(x => x.Message).NotEmpty().WithErrorCode("Empty");
}

/// <summary>
/// A second validator on the same request. §6.3 runs every registered
/// <c>IValidator&lt;T&gt;</c> and gathers the failures, so an empty message has
/// to come back carrying both rules and not the first one to notice.
/// </summary>
/// <remarks>
/// A <c>Must</c> with an explicit code rather than <c>MinimumLength</c>, which
/// reports two failures for one empty string. The test below counts which
/// validators contributed, and a rule that speaks twice would make it agree
/// with itself for the wrong reason.
/// </remarks>
public sealed class PingLengthValidator : AbstractValidator<Ping>
{
    public PingLengthValidator() =>
        RuleFor(x => x.Message).Must(message => message.Length >= 3).WithErrorCode("TooShort");
}

/// <summary>
/// Which behaviours ran, in the order they were entered and left. Scoped, so
/// one dispatch fills one log and a second scope starts empty.
/// </summary>
public sealed class PipelineLog
{
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries => _entries;

    public void Add(string entry) => _entries.Add(entry);
}

/// <summary>
/// Records entry and exit around <c>next()</c>. Nesting is what the ordering
/// test reads: an outer behaviour is entered first and left last, so the log of
/// a correct pipeline is a palindrome of names.
/// </summary>
public abstract class RecordingBehavior<TRequest, TResult>(PipelineLog log)
    : IPipelineBehavior<TRequest, TResult>
{
    public async Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        string name = GetType().Name.Split('`')[0];

        log.Add($"enter {name}");
        TResult result = await next();
        log.Add($"leave {name}");

        return result;
    }
}

public sealed class FirstBehavior<TRequest, TResult>(PipelineLog log)
    : RecordingBehavior<TRequest, TResult>(log);

public sealed class SecondBehavior<TRequest, TResult>(PipelineLog log)
    : RecordingBehavior<TRequest, TResult>(log);

public sealed class ThirdBehavior<TRequest, TResult>(PipelineLog log)
    : RecordingBehavior<TRequest, TResult>(log);

/// <summary>
/// Constrained to commands, the way <c>TransactionBehavior</c> and
/// <c>IdempotencyBehavior</c> are (§6.3). The container is expected to omit it
/// when the closed request type is a query rather than to throw.
/// </summary>
public sealed class CommandOnlyBehavior<TCommand, TResult>(PipelineLog log)
    : RecordingBehavior<TCommand, TResult>(log)
    where TCommand : ICommand<TResult>;

/// <summary>Returns without calling <c>next()</c> — the short-circuit arm.</summary>
public sealed class ShortCircuitBehavior<TRequest, TResult>(PipelineLog log)
    : IPipelineBehavior<TRequest, TResult>
{
    public Task<TResult> HandleAsync(TRequest request, NextDelegate<TResult> next, CancellationToken ct)
    {
        log.Add("short-circuit");
        return Task.FromResult(default(TResult)!);
    }
}

/// <summary>
/// A scoped handler that reports which scope built it. The invoker cache is
/// static and lives for the process (§6.2); this is how a test sees that it
/// caches the invoker and not the scope behind it.
/// </summary>
public sealed record WhichScope : ICommand<Guid>;

public sealed class ScopeMarker
{
    public Guid Id { get; } = Guid.CreateVersion7();
}

public sealed class WhichScopeHandler(ScopeMarker marker) : ICommandHandler<WhichScope, Guid>
{
    public Task<Guid> HandleAsync(WhichScope command, CancellationToken ct) =>
        Task.FromResult(marker.Id);
}
