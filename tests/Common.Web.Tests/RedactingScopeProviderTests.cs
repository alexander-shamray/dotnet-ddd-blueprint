using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// §13.4's scope half — the channel <see cref="SensitiveDataRedactor"/> can
/// read and cannot rewrite.
/// </summary>
public class RedactingScopeProviderTests
{
    private static RedactingScopeProvider Provider() => new(new LoggerExternalScopeProvider());

    private static List<object?> ScopesOf(IExternalScopeProvider provider)
    {
        List<object?> seen = [];

        provider.ForEachScope((scope, state) => state.Add(scope), seen);

        return seen;
    }

    // Through the enumerable interface, because that is what a logging
    // provider reads a scope through — and because the keyed scopes this
    // platform opens are Dictionaries, which are not IReadOnlyList.
    private static List<KeyValuePair<string, object?>> Pairs(object? scope) =>
        [.. (IEnumerable<KeyValuePair<string, object?>>)scope!];

    [Fact]
    public void A_sensitive_key_in_a_scope_is_redacted()
    {
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push(
            new Dictionary<string, object?> { ["Password"] = "hunter2", ["User"] = "ada" });

        IReadOnlyList<KeyValuePair<string, object?>> pairs = Pairs(ScopesOf(provider).Single());

        pairs.Single(p => p.Key == "Password").Value.ShouldBe("[redacted]");
        pairs.Single(p => p.Key == "User").Value.ShouldBe("ada");
    }

    [Fact]
    public void A_connection_string_in_a_scope_is_redacted_by_its_value()
    {
        // The key names nothing sensitive, which is the case the key list
        // cannot reach — and a scope is inherited by every record written
        // inside it, so one such scope is a secret on every line of a request.
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push(
            new Dictionary<string, object?>
            {
                // Spaced separator: valid ADO.NET, and invisible to a literal
                // "password=" check.
                ["Dsn"] = "Server=sql,1433;Database=Catalog;User Id=sa;Password = hunter2"
            });

        Pairs(ScopesOf(provider).Single()).Single().Value.ShouldBe("[redacted]");
    }

    [Fact]
    public void A_scope_with_nothing_sensitive_is_passed_through_unchanged()
    {
        // Identity, not equality: the no-match path must not copy, because it
        // is every scope on every record. The same claim SensitiveDataRedactor
        // makes about attributes, one layer down.
        RedactingScopeProvider provider = Provider();
        Dictionary<string, object?> original = new() { ["RequestType"] = "PlaceOrderCommand" };

        using IDisposable _ = provider.Push(original);

        ScopesOf(provider).Single().ShouldBeSameAs(original);
    }

    [Fact]
    public void A_redacted_scope_does_not_render_the_secret_from_ToString()
    {
        // The load-bearing half. A provider that formats a scope rather than
        // enumerating it would otherwise print the value straight back out of
        // the list this type had just scrubbed.
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push(new Dictionary<string, object?> { ["Token"] = "hunter2" });

        ScopesOf(provider).Single()!.ToString()!.ShouldNotContain("hunter2");
    }

    [Fact]
    public void An_unkeyed_scope_carrying_a_secret_is_replaced()
    {
        // BeginScope(someString) reaches an exporter as a single unkeyed
        // value, so only the value check can say anything about it.
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push("Server=sql;User Id=sa;Password=hunter2");

        ScopesOf(provider).Single().ShouldBe("[redacted]");
    }

    [Fact]
    public void An_unkeyed_ordinary_scope_survives()
    {
        RedactingScopeProvider provider = Provider();

        using IDisposable _ = provider.Push("checkout");

        ScopesOf(provider).Single().ShouldBe("checkout");
    }

    [Fact]
    public void Nesting_is_preserved()
    {
        // A scope provider is a stack, and a wrapper that flattened or dropped
        // one would take §13.3's RequestType or §10.4's CorrelationId with it
        // — a redaction that silently deletes the field an incident is
        // filtered by is not a fix.
        RedactingScopeProvider provider = Provider();

        using IDisposable outer = provider.Push(new Dictionary<string, object?> { ["A"] = "1" });
        using IDisposable inner = provider.Push(new Dictionary<string, object?> { ["Secret"] = "2" });

        List<object?> seen = ScopesOf(provider);

        seen.Count.ShouldBe(2);
        Pairs(seen[0]).Single().Value.ShouldBe("1");
        Pairs(seen[1]).Single().Value.ShouldBe("[redacted]");
    }

    /// <summary>
    /// Captures whatever scope provider the logger factory hands a provider,
    /// which is the whole mechanism this fix rests on.
    /// </summary>
    private sealed class ScopeCapturingProvider : ILoggerProvider, ISupportExternalScope
    {
        public IExternalScopeProvider? Scopes { get; private set; }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider) => Scopes = scopeProvider;

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }
    }

    [Theory]
    [InlineData("instance")]
    [InlineData("factory")]
    [InlineData("type")]
    public void A_provider_registered_first_is_wrapped_rather_than_left_alone(string shape)
    {
        // §13.4 is a guarantee, not a default. TryAddSingleton was the first
        // spelling and it failed open: a host that registered any provider
        // first kept it, unwrapped, and every scope exported raw while
        // IncludeScopes stayed on and the attribute half went on scrubbing
        // beside it — a security control switched off by a registration
        // nobody looked at.
        //
        // All three descriptor shapes, because the registration is rebuilt
        // from the descriptor rather than resolved, and each shape is a
        // different branch of that.
        ServiceCollection services = new();

        switch (shape)
        {
            case "instance":
                services.AddSingleton<IExternalScopeProvider>(new LoggerExternalScopeProvider());
                break;
            case "factory":
                services.AddSingleton<IExternalScopeProvider>(_ => new LoggerExternalScopeProvider());
                break;
            default:
                services.AddSingleton<IExternalScopeProvider, LoggerExternalScopeProvider>();
                break;
        }

        RedactingScopeProvider.WrapScopesForRedaction(services);

        using ServiceProvider root = services.BuildServiceProvider();
        IExternalScopeProvider resolved = root.GetRequiredService<IExternalScopeProvider>();

        resolved.ShouldBeOfType<RedactingScopeProvider>(
            $"a {shape} registration made first must be wrapped, not deferred to");

        using IDisposable _ = resolved.Push(
            new Dictionary<string, object?> { ["Password"] = "hunter2" });

        Pairs(ScopesOf(resolved).Single()).Single().Value.ShouldBe("[redacted]");
    }

    [Fact]
    public async Task A_provider_registered_afterwards_stops_the_host()
    {
        // Wrapping what came before is only half of it. AddCommonWebDefaults
        // runs ahead of a host's own registrations and the container resolves
        // the LAST, so a registration written afterwards deterministically
        // replaces the wrapper and exports every scope raw — the fail-open
        // moved rather than closing when TryAdd became a wrap.
        //
        // Nothing the registration can do prevents that, because the line that
        // beats it has not been written yet. So the guard refuses to start.
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddObservability();
        builder.Services.AddSingleton<IExternalScopeProvider>(new LoggerExternalScopeProvider());

        using IHost host = builder.Build();

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        thrown.Message.ShouldContain(nameof(RedactingScopeProvider));
    }

    [Fact]
    public async Task A_host_whose_provider_is_the_wrapper_starts()
    {
        // The control. Without it the test above passes against a guard that
        // refuses every host, which is the failure this repository files under
        // "a gate only ever observed red".
        HostApplicationBuilder builder = TelemetryHost.Builder();

        builder.AddObservability();

        using IHost host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IExternalScopeProvider>()
            .ShouldBeOfType<RedactingScopeProvider>();
    }

    /// <summary>A provider that records whether it was disposed.</summary>
    private sealed class DisposableScopeProvider : IExternalScopeProvider, IDisposable
    {
        private readonly LoggerExternalScopeProvider _inner = new();

        public bool Disposed { get; private set; }

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            _inner.ForEachScope(callback, state);

        public IDisposable Push(object? state) => _inner.Push(state);

        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void A_provider_this_wrapper_built_is_disposed_with_the_container()
    {
        // Wrapping removes the prior descriptor, so an inner provider built
        // from a factory or an implementation type leaves the container's
        // disposal tracking — this wrapper is what stands between it and a
        // shutdown that never disposes it.
        DisposableScopeProvider built = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(_ => built);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        root.Dispose();

        built.Disposed.ShouldBeTrue("a provider this wrapper constructed is this wrapper's to dispose");
    }

    /// <summary>A provider that can only be disposed asynchronously.</summary>
    private sealed class AsyncOnlyScopeProvider : IExternalScopeProvider, IAsyncDisposable
    {
        private readonly LoggerExternalScopeProvider _inner = new();

        public bool Disposed { get; private set; }

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            _inner.ForEachScope(callback, state);

        public IDisposable Push(object? state) => _inner.Push(state);

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task An_async_only_provider_this_wrapper_built_is_disposed()
    {
        // `IDisposable` alone is not the contract: a provider implementing only
        // `IAsyncDisposable` leaves DI tracking with the descriptor like any
        // other, and a wrapper that handled only the synchronous interface
        // would skip it in silence.
        AsyncOnlyScopeProvider built = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(_ => built);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        await root.DisposeAsync();

        built.Disposed.ShouldBeTrue("an async-only provider is still this wrapper's to dispose");
    }

    [Fact]
    public void An_async_only_provider_is_disposed_on_the_synchronous_path_too()
    {
        // The container prefers DisposeAsync and falls back to Dispose, so the
        // synchronous path has to reach an async-only inner provider as well —
        // otherwise the leak is simply moved to whichever path the host takes.
        AsyncOnlyScopeProvider built = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(_ => built);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        root.Dispose();

        built.Disposed.ShouldBeTrue();
    }

    [Fact]
    public void A_provider_the_caller_supplied_is_left_alone()
    {
        // The other half, and the one that would be a defect in the opposite
        // direction: the container never disposes an instance it did not
        // create, so neither may a wrapper that only happened to be registered
        // around it. Disposing somebody else's object is not tidying up.
        DisposableScopeProvider supplied = new();
        ServiceCollection services = new();

        services.AddSingleton<IExternalScopeProvider>(supplied);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        ServiceProvider root = services.BuildServiceProvider();
        root.GetRequiredService<IExternalScopeProvider>().ShouldBeOfType<RedactingScopeProvider>();

        root.Dispose();

        supplied.Disposed.ShouldBeFalse("an instance the container never created is not the wrapper's to dispose");
    }

    [Fact]
    public void The_wrapper_delegates_to_the_provider_it_replaced()
    {
        // The other half: wrapping must not discard what was there. A wrapper
        // that quietly substituted a fresh provider would pass the test above
        // and lose whatever the host's own provider was for.
        ServiceCollection services = new();
        RecordingScopeProvider inner = new();

        services.AddSingleton<IExternalScopeProvider>(inner);
        RedactingScopeProvider.WrapScopesForRedaction(services);

        using ServiceProvider root = services.BuildServiceProvider();

        using IDisposable _ = root.GetRequiredService<IExternalScopeProvider>()
            .Push(new Dictionary<string, object?> { ["RequestType"] = "PlaceOrderCommand" });

        inner.Pushed.ShouldHaveSingleItem();
    }

    /// <summary>Records what was pushed through it, and behaves otherwise.</summary>
    /// <remarks>
    /// Implements the interface rather than deriving from
    /// <c>LoggerExternalScopeProvider</c> and hiding <c>Push</c> with
    /// <c>new</c>: that method is not virtual, so the hidden one is invisible
    /// to a call made through <see cref="IExternalScopeProvider"/> — which is
    /// the only way this is ever called. The first version of this double did
    /// exactly that and failed, correctly.
    /// </remarks>
    private sealed class RecordingScopeProvider : IExternalScopeProvider
    {
        private readonly LoggerExternalScopeProvider _inner = new();

        public List<object?> Pushed { get; } = [];

        public void ForEachScope<TState>(Action<object?, TState> callback, TState state) =>
            _inner.ForEachScope(callback, state);

        public IDisposable Push(object? state)
        {
            Pushed.Add(state);

            return _inner.Push(state);
        }
    }

    [Fact]
    public void The_logger_factory_hands_providers_the_registered_scope_provider()
    {
        // Measured rather than assumed, and it is the assumption everything
        // above depends on: LoggerFactory has a constructor taking an
        // IExternalScopeProvider, and the container picks the greediest
        // constructor it can satisfy — so registering one is what makes this
        // wrapper reach OpenTelemetry's provider at all. If a release ever
        // stopped resolving it, every test above would still pass and nothing
        // in production would be redacted.
        ServiceCollection services = new();
        ScopeCapturingProvider capture = new();

        services.AddSingleton<IExternalScopeProvider>(Provider());
        services.AddLogging(logging => logging.AddProvider(capture));

        using ServiceProvider root = services.BuildServiceProvider();
        ILogger logger = root.GetRequiredService<ILoggerFactory>().CreateLogger("test");

        // ILogger.BeginScope is nullable-returning; Push above is not.
        using IDisposable? _ = logger.BeginScope(
            new Dictionary<string, object?> { ["Password"] = "hunter2" });

        IExternalScopeProvider handed = capture.Scopes.ShouldNotBeNull();

        Pairs(ScopesOf(handed).Single()).Single().Value.ShouldBe("[redacted]");
    }
}
