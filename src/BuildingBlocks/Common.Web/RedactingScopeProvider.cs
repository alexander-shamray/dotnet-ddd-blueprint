using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Common.Web;

/// <summary>
/// §13.4's never-log rule applied to log <em>scopes</em>, which
/// <see cref="SensitiveDataRedactor"/> cannot reach.
/// </summary>
/// <remarks>
/// <b>Why this is a scope provider and not more of the processor.</b>
/// <c>AddObservability</c> sets <c>IncludeScopes</c> (§13.2), so every record
/// carries the scopes open around it — and an OpenTelemetry
/// <c>BaseProcessor&lt;LogRecord&gt;</c> can only read them: <c>LogRecord</c>
/// exposes <c>ForEachScope</c> and no settable scope provider. A processor
/// could therefore notice a secret in a scope and would have no way to remove
/// it. The redaction has to happen where the scope is <em>read</em>, which is
/// here.
/// <para>
/// <b>What it covers, and why that is wider than the platform's own two
/// scopes.</b> <c>LoggerFactory</c> takes an <see cref="IExternalScopeProvider"/>
/// from the container and hands it to every provider, so this wraps the scopes
/// EF Core and MassTransit open as well as <c>LoggingBehavior</c>'s
/// <c>RequestType</c> (§13.3) and <c>UseCorrelationId</c>'s
/// <c>CorrelationId</c> (§10.4). That is the point: the file this replaces
/// argued the platform's two scopes were provably safe, and one of them was
/// carrying a client-supplied header at the time.
/// </para>
/// <para>
/// <b>It redacts on the way out, not on the way in.</b> <see cref="Push"/>
/// stores the caller's object untouched, because a scope is also a live object
/// the application may read back; only the enumeration a logging provider
/// performs is rewritten. That also keeps the cost on the path that logs
/// rather than on the path that opens a scope.
/// </para>
/// </remarks>
/// <param name="inner">
/// The provider whose scopes are being wrapped — <c>LoggerExternalScopeProvider</c>
/// in every host, but taken as a parameter so the wrapping is testable.
/// </param>
public sealed class RedactingScopeProvider(IExternalScopeProvider inner) : IExternalScopeProvider
{
    /// <summary>
    /// Registers this wrapper as the container's <see cref="IExternalScopeProvider"/>,
    /// around whatever was registered before it.
    /// </summary>
    /// <remarks>
    /// <b>Wrapping rather than deferring, because §13.4 is a guarantee and not
    /// a default.</b> <c>TryAddSingleton</c> was the first spelling: a host
    /// that had registered any provider first kept it, unwrapped, and every
    /// scope exported raw — the control switched off by a registration nobody
    /// looked at, with <c>IncludeScopes</c> still on and the attribute half
    /// still scrubbing beside it. There is no host here that wants its own
    /// provider, and if one arrives it wants a redacted one.
    /// <para>
    /// The prior descriptor is removed and rebuilt inside the factory rather
    /// than resolved, because resolving <see cref="IExternalScopeProvider"/>
    /// from within its own factory is unbounded recursion. Only the last
    /// non-keyed registration is wrapped, for the reason the container itself
    /// gives: single-service resolution returns the last, so the earlier ones
    /// were already unreachable.
    /// </para>
    /// </remarks>
    /// <param name="services">The host's service collection.</param>
    public static IServiceCollection WrapScopesForRedaction(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        ServiceDescriptor? existing = services.LastOrDefault(
            d => d.ServiceType == typeof(IExternalScopeProvider) && !d.IsKeyedService);

        if (existing is not null)
            services.Remove(existing);

        services.AddSingleton<IExternalScopeProvider>(
            sp => new RedactingScopeProvider(Inner(sp, existing)));

        // Wrapping what came BEFORE is only half of it, and the first version
        // of this method stopped there. AddCommonWebDefaults runs ahead of a
        // host's own registrations, and the container resolves the LAST — so
        // an AddSingleton<IExternalScopeProvider>(…) written afterwards
        // deterministically replaces this wrapper and exports every scope raw.
        // The fail-open moved rather than closing.
        //
        // Nothing this method does can prevent that, because the registration
        // it would have to beat has not happened yet. What it can do is refuse
        // to run: the guard resolves the provider once the container is built
        // and stops the host if it is not the wrapper.
        services.AddHostedService<ScopeRedactionGuard>();

        return services;
    }

    // The three shapes a descriptor can carry. A keyed one never reaches here
    // — the query above excludes them, because a keyed registration is not
    // what LoggerFactory resolves.
    private static IExternalScopeProvider Inner(IServiceProvider sp, ServiceDescriptor? existing)
    {
        if (existing is null)
            return new LoggerExternalScopeProvider();

        if (existing.ImplementationInstance is IExternalScopeProvider instance)
            return instance;

        if (existing.ImplementationFactory is not null)
            return (IExternalScopeProvider)existing.ImplementationFactory(sp);

        return (IExternalScopeProvider)ActivatorUtilities.CreateInstance(
            sp,
            existing.ImplementationType!);
    }

    private readonly IExternalScopeProvider _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // A static lambda with the caller's callback carried in the state, so
        // nothing is captured. The obvious spelling closes over `callback` and
        // allocates a display class every time a provider enumerates scopes —
        // which is every log record, since §10.4 opens a correlation scope on
        // every request. The same argument the copy-on-match paths below are
        // written for, and it was missed on the one method they all run
        // through.
        _inner.ForEachScope(
            static (object? scope, (Action<object?, TState> Callback, TState State) s) =>
                s.Callback(Redact(scope), s.State),
            (Callback: callback, State: state));
    }

    /// <inheritdoc />
    public IDisposable Push(object? state) => _inner.Push(state);

    private const string Redacted = "[redacted]";

    /// <summary>
    /// The scope a logging provider sees: the original where nothing matched,
    /// and a copy with the sensitive values replaced where something did.
    /// </summary>
    private static object? Redact(object? scope)
    {
        // IEnumerable rather than IReadOnlyList, and the difference is the
        // whole of the keyed case: BeginScope(new Dictionary<,>) — which is
        // what §10.4 and §13.3 both open — produces a Dictionary, and a
        // Dictionary is NOT an IReadOnlyList. Matching on the list interface
        // alone left every scope this platform actually opens unredacted while
        // the unit tests over MEL's own FormattedLogValues stayed green.
        if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            return RedactPairs(scope, pairs);

        // A scope with no keys at all reaches the exporter as a single unkeyed
        // value, so only the value check can say anything about it. Narrow, and
        // it is the shape BeginScope(someString) produces.
        return SensitiveKeys.LooksLikeSecret(scope) ? Redacted : scope;
    }

    // Scanned before it is copied, so the common case — nothing sensitive —
    // returns the caller's own object and allocates only the enumerator. The
    // second pass is paid on the match path alone, which is the one that was
    // about to export a secret.
    private static object RedactPairs(object scope, IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        if (!AnySensitive(pairs))
            return scope;

        List<KeyValuePair<string, object?>> scrubbed = [];

        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            scrubbed.Add(IsSensitive(pair)
                ? new KeyValuePair<string, object?>(pair.Key, Redacted)
                : pair);
        }

        return new RedactedScope(scrubbed);
    }

    private static bool AnySensitive(IEnumerable<KeyValuePair<string, object?>> pairs)
    {
        foreach (KeyValuePair<string, object?> pair in pairs)
        {
            if (IsSensitive(pair))
                return true;
        }

        return false;
    }

    private static bool IsSensitive(KeyValuePair<string, object?> pair) =>
        SensitiveKeys.Matches(pair.Key) || SensitiveKeys.LooksLikeSecret(pair.Value);

    /// <summary>
    /// A scrubbed scope, in the shape every reader of a scope expects.
    /// </summary>
    /// <remarks>
    /// <b><c>ToString</c> is overridden, and that is the load-bearing half.</b>
    /// MEL's own scope type renders its values from <c>ToString</c>, and a
    /// provider that formats a scope rather than enumerating it would
    /// otherwise print the secret straight back out of a list this class had
    /// just scrubbed — the same failure <see cref="SensitiveDataRedactor"/>'s
    /// <c>FormattedMessage</c> rewrite exists to prevent, one layer over.
    /// </remarks>
    /// <summary>
    /// Refuses to start a host whose <see cref="IExternalScopeProvider"/> is
    /// not a <see cref="RedactingScopeProvider"/>.
    /// </summary>
    /// <remarks>
    /// <b>A resolve-time check, because a registration-time one cannot see the
    /// future.</b> §13.4 is a platform guarantee, and the registration that
    /// would defeat it is written after <c>AddCommonWebDefaults</c> has already
    /// run. Failing the host is the direction §13.5's readiness guard already
    /// takes for the same reason: a control that is silently absent is worse
    /// than a host that will not boot, because only one of the two is
    /// noticed.
    /// <para>
    /// It reports rather than repairs. Re-registering the wrapper here would
    /// leave a host running with a provider its own author did not choose, and
    /// the message names the type so the fix is the caller's to make.
    /// </para>
    /// </remarks>
    private sealed class ScopeRedactionGuard(IExternalScopeProvider scopes) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (scopes is not RedactingScopeProvider)
            {
                throw new InvalidOperationException(
                    $"IExternalScopeProvider resolves to {scopes.GetType().FullName}, not " +
                    $"{nameof(RedactingScopeProvider)}, so §13.4's scope redaction is switched " +
                    "off and every log scope exports raw. A registration made after " +
                    "AddCommonWebDefaults wins, because the container resolves the last one — " +
                    "remove it, or wrap it by calling RedactingScopeProvider" +
                    ".WrapScopesForRedaction after it.");
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RedactedScope(List<KeyValuePair<string, object?>> pairs)
        : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public int Count => pairs.Count;

        public KeyValuePair<string, object?> this[int index] => pairs[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => pairs.GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
            GetEnumerator();

        public override string ToString() => string.Join(", ", pairs.Select(p => $"{p.Key}={p.Value}"));
    }
}
