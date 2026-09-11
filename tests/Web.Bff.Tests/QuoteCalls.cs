using System.Net.Http.Json;
using Web.Bff.Endpoints;

namespace Web.Bff.Tests;

/// <summary>
/// How this suite asks for a quote, in one place. Several classes here drive
/// the same endpoint, and before the request carried a body each of them
/// spelled the URL itself — which was tolerable while a quote was a query
/// string and is not now that it is a record.
/// </summary>
/// <remarks>
/// The body is built out of <see cref="QuoteRequest"/> rather than an
/// anonymous object, deliberately: a test that serialises its own JSON shape
/// keeps passing after the record's members move, which is exactly the drift a
/// contract test exists to catch.
/// </remarks>
internal static class QuoteCalls
{
    internal const string Path = "/v1/checkout/quote";

    /// <summary>The raw reply, for the tests whose subject is the status.</summary>
    internal static Task<HttpResponseMessage> PostQuote(
        this HttpClient client,
        string currency,
        CancellationToken ct,
        params (Guid ProductId, int Quantity)[] lines) =>
        client.PostQuote(
            new QuoteRequest(currency, [.. lines.Select(line => new QuoteRequestLine(line.ProductId, line.Quantity))]),
            ct);

    /// <summary>
    /// The same, for a request the tuple form cannot express — a null line
    /// list, or one built somewhere other than a test method.
    /// </summary>
    internal static Task<HttpResponseMessage> PostQuote(
        this HttpClient client,
        QuoteRequest request,
        CancellationToken ct) =>
        client.PostAsJsonAsync(Path, request, ct);

    /// <summary>
    /// The quote itself, for the tests whose subject is the arithmetic. Fails
    /// loudly on a non-success status rather than handing back a null the
    /// assertions would blame on the body.
    /// </summary>
    internal static async Task<QuoteResponse?> Quote(
        this HttpClient client,
        string currency,
        CancellationToken ct,
        params (Guid ProductId, int Quantity)[] lines)
    {
        using HttpResponseMessage response = await client.PostQuote(currency, ct, lines);

        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<QuoteResponse>(ct);
    }
}
