using Catalog.Domain.Common;
using Catalog.Domain.Products;
using Common.Application;

namespace Catalog.TestSupport.Outbox;

/// <summary>
/// A command that reaches the outbox and then fails, which is the only shape
/// that can prove a staged row rolls back with the aggregate.
/// </summary>
/// <remarks>
/// <b>Failing earlier proves nothing, and that is why this type exists.</b> A
/// command rejected by the validator never opens a transaction; one whose
/// handler returns <c>Result.Failure</c> returns from §6.3's behaviour
/// <em>before</em> <c>DispatchAsync</c> runs, so nothing is staged in either
/// case and the assertion "no outbox row" holds for the wrong reason — it
/// would still hold if rows were written outside the aggregate's transaction
/// altogether.
/// <para>
/// The one-aggregate assertion is the failure point that sits on the far side
/// of staging: <c>TransactionBehavior</c> dispatches, <em>then</em> counts, and
/// throws <c>InvariantViolationException</c> when a command has modified more
/// than one root (§2.3, principle 3). So this handler publishes two products,
/// both events are staged, and the throw takes the whole transaction down with
/// the rows in it. No fault injection and no test-only seam in production
/// code: this is the behaviour §6.3 already promises, used as a lever.
/// </para>
/// </remarks>
public sealed record StageThenFailCommand(string Name) : ICommand<Result>;

public sealed class StageThenFailHandler(IProductRepository products, TimeProvider clock)
    : ICommandHandler<StageThenFailCommand, Result>
{
    public Task<Result> HandleAsync(StageThenFailCommand command, CancellationToken ct)
    {
        // Two roots, so the count is 2 and the assertion fires — after the
        // dispatcher has staged a ProductPublishedDomainEvent for each.
        products.Add(Product.Publish(command.Name, null, Money.Of(1m, "EUR"), clock.GetUtcNow()));
        products.Add(Product.Publish(command.Name, null, Money.Of(2m, "EUR"), clock.GetUtcNow()));

        return Task.FromResult(Result.Success());
    }
}
