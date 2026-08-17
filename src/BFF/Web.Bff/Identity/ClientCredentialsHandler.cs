using System.Net.Http.Headers;
using Microsoft.Extensions.Options;

namespace Web.Bff.Identity;

/// <summary>
/// §11.5's handler, attached to every outbound client so that no call site has
/// to remember it.
/// </summary>
/// <remarks>
/// <b>It must sit inside the resilience pipeline</b>, which is what registering
/// it <i>after</i> <c>AddStandardResilienceHandler</c> does (§9.7). Outside it,
/// every attempt of a retry reuses the token the first attempt built — so the
/// one case a retry is most likely to fix, a token that expired in flight, is
/// the one case it cannot.
/// </remarks>
public sealed class ClientCredentialsHandler(ITokenCache tokens, IOptions<ServiceIdentityOptions> identity)
    : DelegatingHandler
{
    /// <remarks>
    /// The parameter is <c>cancellationToken</c> and not this repository's
    /// usual <c>ct</c>, because CA1725 requires an override to keep the base
    /// declaration's name and ADR-019 makes that an error. §11.5 prints
    /// <c>ct</c> and was amended in this change — the same correction §7.2's
    /// <c>ConfigureConventions</c> sample already took, and for the same
    /// reason: a reader consulting the framework's documentation is reading
    /// about the base name.
    /// </remarks>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        // Cached until shortly before expiry; one token fetch serves many calls.
        string token = await tokens.GetAsync(identity.Value.Scope, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
