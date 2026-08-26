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
    private readonly IExternalScopeProvider _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));

    /// <inheritdoc />
    public void ForEachScope<TState>(Action<object?, TState> callback, TState state)
    {
        ArgumentNullException.ThrowIfNull(callback);

        _inner.ForEachScope((scope, s) => callback(Redact(scope), s), state);
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
