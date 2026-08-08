using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

/// <summary>
/// §6.3's behaviour against a recording unit of work. The contract is a
/// sequence — enter the unit, run the handler, dispatch, count, save — and
/// every test here is an assertion about which of those happened and in what
/// order, which is why the fakes share one <see cref="PipelineLog"/>.
/// </summary>
public class TransactionBehaviorTests
{
    [Fact]
    public async Task A_successful_command_enters_the_unit_dispatches_then_saves_once()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "save"],
            "dispatch before save is what puts outbox rows into the same save, " +
            "and dispatch before the count is §6.3's own comment");
    }

    [Fact]
    public async Task A_failed_result_skips_dispatch_and_save()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();

        Result result = await Dispatch(scope, new Reject());

        result.IsFailure.ShouldBeTrue();
        Log(scope).Entries.ShouldBe(
            ["execute"],
            "a rejected command must not stage events or save — §6.3's failure guard");
    }

    [Fact]
    public async Task A_command_whose_result_is_not_a_Result_commits_normally()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hi");
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "save"],
            "the failure guard is a pattern match on Result; any other result type commits");
    }

    [Fact]
    public async Task A_nested_dispatch_runs_inside_the_open_unit_without_a_second_one()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().HasActiveTransaction = true;

        Result result = await Dispatch(scope, new Approve());

        result.IsSuccess.ShouldBeTrue();
        Log(scope).Entries.ShouldBeEmpty(
            "already inside a transaction, the behaviour is a passthrough — " +
            "the outer dispatch owns the dispatch-count-save sequence");
    }

    [Fact]
    public async Task A_command_modifying_two_aggregates_throws_and_saves_nothing()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<FakeUnitOfWork>().AggregateCount = 2;

        InvariantViolationException exception =
            await Should.ThrowAsync<InvariantViolationException>(() => Dispatch(scope, new Approve()));

        exception.Message.ShouldContain(nameof(Approve));
        exception.Message.ShouldContain("2");

        // Two counts, not one: the guard reads the property and the message
        // interpolates it again — §6.3's shape, and only on the throwing path.
        Log(scope).Entries.ShouldBe(
            ["execute", "dispatch", "count", "count"],
            "the guard fires after dispatch — §6.3 counts staged rows too — and before save");
    }

    [Fact]
    public async Task A_throwing_handler_saves_nothing()
    {
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Boom(), TestContext.Current.CancellationToken));

        Log(scope).Entries.ShouldBe(
            ["execute"],
            "an exception unwinds through the unit; nothing is dispatched or saved");
    }

    [Fact]
    public async Task A_query_never_touches_the_unit_of_work()
    {
        // Appendix C's third test, on the real type: CommandOnlyBehavior
        // proved the container honours constraints, and this proves
        // TransactionBehavior actually carries one.
        using ServiceProvider provider = BuildProvider();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        result.ShouldBe("answer:why");
        Log(scope).Entries.ShouldBeEmpty(
            "a query opening a transaction is the defect this catches — §6.3");
    }

    private static ServiceProvider BuildProvider() =>
        TestContainer.Build(services =>
        {
            services.AddScoped<FakeUnitOfWork>();
            services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<FakeUnitOfWork>());
            services.AddScoped<IDomainEventDispatcher, FakeDomainEventDispatcher>();
            services.AddScoped(typeof(IPipelineBehavior<,>), typeof(TransactionBehavior<,>));
        });

    private static Task<Result> Dispatch(IServiceScope scope, ICommand<Result> command) =>
        scope.ServiceProvider
            .GetRequiredService<IDispatcher>()
            .SendAsync(command, TestContext.Current.CancellationToken);

    private static PipelineLog Log(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<PipelineLog>();
}
