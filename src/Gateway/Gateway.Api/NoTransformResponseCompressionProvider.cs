using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Gateway.Api;

/// <summary>
/// The framework's compression provider, refusing any response that carries
/// <c>Cache-Control: no-transform</c> (ADR-020).
/// </summary>
/// <remarks>
/// <para>
/// <b>RFC 9111 §5.2.2.6 is not advisory, and ASP.NET Core does not implement
/// it.</b> The directive "indicates that an intermediary (regardless of
/// whether it implements a cache) MUST NOT transform the content", and
/// applying a content coding is exactly such a transformation
/// (RFC 9110 §7.7). A YARP gateway is an intermediary, so compressing past
/// the directive is a protocol violation — measured before this type existed:
/// an 8 KiB body sent under <c>no-transform</c> came back gzipped with the
/// directive intact.
/// </para>
/// <para>
/// <b>Honouring it is what makes ADR-020's opt-out the standard one.</b> The
/// alternative a downstream had was <c>Content-Encoding: identity</c>, which
/// works only because the middleware skips anything already carrying that
/// header — a side effect of the double-compression guard rather than a
/// refusal, and a content coding this platform would then be exposing to
/// clients for no reason of theirs. <c>no-transform</c> travels instead: the
/// ingress, the CDN and every cache on the path read it, where a content
/// coding speaks only to whatever reads the response next.
/// </para>
/// <para>
/// A subclass rather than a decorator because <c>ShouldCompressResponse</c> is
/// virtual and the base type is the one the framework would otherwise
/// construct — there is nothing to reimplement, only a case to add in front of
/// it.
/// </para>
/// </remarks>
internal sealed class NoTransformResponseCompressionProvider(
    IServiceProvider services,
    IOptions<ResponseCompressionOptions> options)
    : ResponseCompressionProvider(services, options)
{
    public override bool ShouldCompressResponse(HttpContext context)
    {
        string cacheControl = context.Response.Headers.CacheControl.ToString();

        // Checked before the base call, so the directive wins outright rather
        // than depending on what else would have been decided. TryParse
        // returning false means the header says nothing this cares about,
        // which is the same answer as its absence.
        if (CacheControlHeaderValue.TryParse(cacheControl, out CacheControlHeaderValue? parsed) && parsed.NoTransform)
            return false;

        return base.ShouldCompressResponse(context);
    }
}
