using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class DispatcherTests
{
    [Fact]
    public async Task A_command_reaches_its_handler()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.SendAsync(new Ping("hi"), TestContext.Current.CancellationToken);

        result.ShouldBe("pong:hi");
    }

    [Fact]
    public async Task A_query_reaches_its_handler()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string result = await dispatcher.QueryAsync(new Ask("why"), TestContext.Current.CancellationToken);

        result.ShouldBe("answer:why");
    }

    [Fact]
    public async Task A_command_with_no_handler_throws_rather_than_returning_nothing()
    {
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.SendAsync(new Unhandled(), TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(Unhandled));
    }

    [Fact]
    public void The_dispatcher_cannot_be_resolved_from_the_root_provider()
    {
        // Handlers are scoped, so the dispatcher has to be (§6.2). Registered
        // as a singleton it would capture the root provider and every request
        // would share one handler instance — and one DbContext, once PR-08
        // puts one behind it.
        using ServiceProvider provider = TestContainer.Build();

        Should.Throw<InvalidOperationException>(() =>
        {
            provider.GetRequiredService<IDispatcher>();
        });
    }

    [Fact]
    public async Task The_cached_invoker_resolves_the_handler_from_the_calling_scope()
    {
        // The invoker cache is static and outlives every scope in the process
        // (§6.2). What it must not do is close over the provider that first
        // built it: a second dispatch from a second scope has to get that
        // scope's handler, not the first one's.
        using ServiceProvider provider = TestContainer.Build();

        Guid first;
        Guid second;

        using (IServiceScope scope = provider.CreateScope())
        {
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            first = await dispatcher.SendAsync(new WhichScope(), TestContext.Current.CancellationToken);
        }

        using (IServiceScope scope = provider.CreateScope())
        {
            IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
            second = await dispatcher.SendAsync(new WhichScope(), TestContext.Current.CancellationToken);
        }

        first.ShouldNotBe(second);
    }

    [Fact]
    public async Task The_same_request_type_dispatches_the_same_way_twice()
    {
        // The second call reads the cached invoker rather than building one.
        // Both arms are asserted because a cache that hands back the wrong
        // entry fails only on the second call, and a test that dispatches once
        // never reaches the line that matters.
        using ServiceProvider provider = TestContainer.Build();
        using IServiceScope scope = provider.CreateScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        string first = await dispatcher.SendAsync(new Ping("one"), TestContext.Current.CancellationToken);
        string second = await dispatcher.SendAsync(new Ping("two"), TestContext.Current.CancellationToken);

        first.ShouldBe("pong:one");
        second.ShouldBe("pong:two");
    }
}
