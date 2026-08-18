namespace Web.Bff;

/// <summary>
/// The platform's one synchronous downstream hop, named in one place (§9.7,
/// ADR-017).
/// </summary>
public static class PricingHop
{
    /// <summary>
    /// The <see cref="IHttpClientFactory"/> name the gRPC client registers
    /// under, given explicitly rather than defaulted to the client type's name.
    /// </summary>
    /// <remarks>
    /// The resilience options are registered under a name derived from this
    /// one, and a test asserting §9.7's timeout hierarchy has to be able to ask
    /// for them. Defaulting would make that name <c>PricingClient</c> — a
    /// generated identifier, which is a fragile thing for a test to spell.
    /// </remarks>
    public const string ClientName = "catalog-pricing";

    /// <summary>
    /// The options name <c>AddStandardResilienceHandler</c> registers
    /// <c>HttpStandardResilienceOptions</c> under for this client.
    /// </summary>
    /// <remarks>
    /// A convention of the library rather than a contract, so nothing here
    /// trusts it: <c>ResilienceHierarchyTests</c> reads the options through this
    /// name and asserts the configured values. A wrong name yields a
    /// default-constructed instance whose 30 s total request timeout breaches
    /// the hierarchy at once — so the test fails loudly rather than passing
    /// against defaults, which is the direction that matters.
    /// </remarks>
    public const string ResilienceOptionsName = $"{ClientName}-standard";

    /// <summary>
    /// Catalog's gRPC endpoint.
    /// </summary>
    /// <remarks>
    /// <b>http, not https</b>: TLS terminates at the ingress and traffic inside
    /// the cluster is plain (§10.1). <b>Port 8081, not 8080</b>: a cleartext
    /// Kestrel endpoint cannot serve HTTP/1.1 and h2c at once — measured, and
    /// argued in <c>Catalog.Api/appsettings.json</c> — so Catalog declares a
    /// second, HTTP/2-only endpoint and this is it. §9.7 printed 8080 and was
    /// amended in this change.
    /// <para>
    /// A literal rather than a configuration key, deliberately. The host is the
    /// Kubernetes Service name, which is the same string in Compose because the
    /// container is named to match — so the value does not differ between
    /// environments, and §15.4's rule is that something which does not vary is
    /// not configuration. It is also the same name YARP routes to (§10.2): the
    /// service's name <i>is</i> the routing configuration, in both files.
    /// </para>
    /// </remarks>
    public static readonly Uri Address = new("http://catalog-api:8081");
}
