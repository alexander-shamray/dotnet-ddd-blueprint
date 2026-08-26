using Microsoft.Extensions.DependencyInjection;
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
                ["Dsn"] = "Server=sql,1433;Database=Catalog;User Id=sa;Password=hunter2"
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
