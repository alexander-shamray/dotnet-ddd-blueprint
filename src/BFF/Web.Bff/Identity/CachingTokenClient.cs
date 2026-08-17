using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Common.Web;
using Microsoft.Extensions.Options;

namespace Web.Bff.Identity;

/// <summary>
/// §11.5's client-credentials grant, cached. A singleton, because the token it
/// holds is the host's own and not a caller's — nothing here is per request,
/// and a scoped cache would fetch a token per inbound call and turn one
/// synchronous hop into two.
/// </summary>
/// <remarks>
/// <b>It fetches over its own named client, and that is not tidiness.</b> The
/// client named below carries no <see cref="ClientCredentialsHandler"/>; if it
/// did, every token fetch would attach a token, which needs a token fetch. The
/// recursion terminates only by stack overflow, and it would be invisible in
/// the registration because the handler is attached to the <i>other</i> client.
/// </remarks>
public sealed partial class CachingTokenClient(
    IHttpClientFactory clients,
    IOptions<ServiceIdentityOptions> identity,
    TimeProvider clock,
    ILogger<CachingTokenClient> logger) : ITokenCache, IDisposable
{
    /// <summary>The named <see cref="HttpClient"/> this fetches over (§11.5).</summary>
    public const string HttpClientName = "identity";

    /// <summary>
    /// How long before real expiry a cached token stops being handed out.
    /// </summary>
    /// <remarks>
    /// Not a rounding allowance. The token has to survive the whole outbound
    /// call it is attached to, and §9.7 gives that call up to five seconds of
    /// retries; a token handed out with two seconds left would expire
    /// <i>between</i> attempt one and attempt three, which is the exact failure
    /// the handler's position inside the resilience pipeline exists to recover
    /// from and which this makes rare rather than routine. Thirty seconds
    /// covers the budget with room for drift between this host's clock and the
    /// provider's — the same drift §11.3's <c>ClockSkew</c> allows on the way
    /// in.
    /// </remarks>
    private static readonly TimeSpan ExpiryGuard = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, CachedToken> _tokens = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Uri? _tokenEndpoint;

    public async Task<string> GetAsync(string scope, CancellationToken ct)
    {
        if (TryRead(scope, out string cached))
            return cached;

        // One fetch at a time across every scope, not one per scope. There is
        // exactly one scope in this platform (§11.5), so a per-scope gate would
        // be a dictionary of semaphores guarding one entry — and the shared
        // gate also serialises the discovery fetch below, which is the other
        // thing a burst of first requests would otherwise duplicate.
        await _gate.WaitAsync(ct);

        try
        {
            // Re-read inside the gate: everything queued behind the first
            // fetcher is now covered by the token that fetcher obtained, and
            // going on would give the provider one request per waiter for a
            // value already in hand.
            if (TryRead(scope, out cached))
                return cached;

            CachedToken token = await FetchAsync(scope, ct);
            _tokens[scope] = token;

            return token.AccessToken;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool TryRead(string scope, out string accessToken)
    {
        if (_tokens.TryGetValue(scope, out CachedToken? token) &&
            token.ExpiresAt - ExpiryGuard > clock.GetUtcNow())
        {
            accessToken = token.AccessToken;

            return true;
        }

        accessToken = "";

        return false;
    }

    private async Task<CachedToken> FetchAsync(string scope, CancellationToken ct)
    {
        HttpClient client = clients.CreateClient(HttpClientName);
        Uri endpoint = _tokenEndpoint ??= await DiscoverTokenEndpointAsync(client, ct);

        // The grant, form-encoded, with the secret in the body rather than in a
        // basic-auth header. Both are permitted by RFC 6749 §2.3.1; the body
        // form is what Keycloak's own examples use, and it keeps the secret out
        // of the one header every proxy in the world is willing to log.
        using FormUrlEncodedContent form = new(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = identity.Value.ClientId,
            ["client_secret"] = identity.Value.ClientSecret,
            ["scope"] = scope
        });

        using HttpResponseMessage response = await client.PostAsync(endpoint, form, ct);
        string body = await response.Content.ReadAsStringAsync(ct);

        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(Failure(response.StatusCode, body));

        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;

        if (!root.TryGetProperty("access_token", out JsonElement accessToken) ||
            accessToken.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "The token endpoint answered success with no access_token. The body is deliberately not " +
                "echoed here: it is the one payload this host handles that carries a bearer token (§13.4).");
        }

        // expires_in is seconds, and RFC 6749 §5.1 makes it OPTIONAL. Absent,
        // the token is treated as already expired, which costs a fetch per call
        // and is the safe direction — the unsafe one is assuming an hour and
        // attaching a dead token for fifty-nine minutes of it.
        int lifetime = root.TryGetProperty("expires_in", out JsonElement expiresIn) &&
            expiresIn.TryGetInt32(out int seconds)
            ? seconds
            : 0;

        TokenFetched(logger, scope, lifetime);

        return new CachedToken(accessToken.GetString()!, clock.GetUtcNow().AddSeconds(lifetime));
    }

    /// <summary>
    /// Source-generated, because CA1848 and CA1873 are errors under ADR-019 and
    /// a plain <c>LogDebug</c> allocates its argument array whether or not
    /// Debug is enabled. The same answer PR-04 gave <c>LoggingBehavior</c>:
    /// meet the rule by changing the code, not by waiving it.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Fetched a client-credentials token for scope {Scope}, valid for {Lifetime}s.")]
    private static partial void TokenFetched(ILogger logger, string scope, int lifetime);

    /// <summary>
    /// The gate is a <see cref="SemaphoreSlim"/>, which is disposable, and
    /// CA1001 is right to insist even though this type is a singleton that
    /// outlives everything but the process — a test builds and drops many
    /// hosts, and "the container disposes it" is only true because this
    /// implements the interface that lets it.
    /// </summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>
    /// The token endpoint, read from the provider's discovery document rather
    /// than built by appending a Keycloak-shaped path to the authority.
    /// </summary>
    /// <remarks>
    /// It is the same document §11.3's JWT handler already fetches, from the
    /// same configuration key, so the credentials this host presents and the
    /// tokens it accepts cannot end up pointed at different realms. Appending
    /// <c>/protocol/openid-connect/token</c> would work today and would encode
    /// the provider's URL shape into the host.
    /// </remarks>
    private static async Task<Uri> DiscoverTokenEndpointAsync(HttpClient client, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(".well-known/openid-configuration", ct);
        response.EnsureSuccessStatusCode();

        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));

        if (!document.RootElement.TryGetProperty("token_endpoint", out JsonElement endpoint) ||
            endpoint.ValueKind != JsonValueKind.String ||
            !Uri.TryCreate(endpoint.GetString(), UriKind.Absolute, out Uri? parsed))
        {
            throw new InvalidOperationException(
                $"The discovery document at '{client.BaseAddress}' declares no usable token_endpoint. " +
                $"'{AuthenticationExtensions.AuthorityKey}' names an OpenID provider (§11.3), and this " +
                "host needs that same one to mint its own token (§11.5).");
        }

        return parsed;
    }

    /// <summary>
    /// The failure message, with RFC 6749's <c>error</c> member lifted out and
    /// the body left behind.
    /// </summary>
    /// <remarks>
    /// The raw body is never included. A token endpoint answers a
    /// <i>successful</i> grant with a bearer token, and a failure path that
    /// echoes whatever arrived is one provider quirk away from writing that
    /// token into a log — where §13.4's redactor cannot reach it, because it
    /// scrubs keyed attributes and says in its own file that it cannot see a
    /// secret interpolated into a message.
    /// </remarks>
    private static string Failure(HttpStatusCode status, string body)
    {
        string detail = "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out JsonElement error) &&
                error.ValueKind == JsonValueKind.String)
            {
                detail = $" ({error.GetString()})";
            }
        }
        catch (JsonException)
        {
            // A non-JSON body from a token endpoint says nothing worth
            // repeating, and repeating it is the risk this method exists to
            // avoid.
        }

        // The status formatted invariantly and interpolated as a string, rather
        // than string.Create over the whole message: the concatenation below
        // makes this a plain string expression, not an interpolated-string
        // handler, and string.Create's handler overload then cannot bind
        // (CS1620). One value here is culture-sensitive and this is it.
        string code = ((int)status).ToString(CultureInfo.InvariantCulture);

        return $"The token endpoint refused this host's client credentials with {code}{detail}. " +
            $"'{ServiceIdentityOptions.SectionName}' is the only credential set in the platform (§11.5), " +
            "so this is a deployment fault rather than a caller's.";
    }

    private sealed record CachedToken(string AccessToken, DateTimeOffset ExpiresAt);
}
