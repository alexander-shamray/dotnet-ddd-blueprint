using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Gateway.Api;

/// <summary>
/// The framework's compression provider, refusing any exchange in which either
/// side carries <c>Cache-Control: no-transform</c> (ADR-020).
/// </summary>
/// <remarks>
/// <para>
/// <b>RFC 9111 §5.2.2.6 is not advisory, and ASP.NET Core does not implement
/// it.</b> The response directive "indicates that an intermediary (regardless
/// of whether it implements a cache) MUST NOT transform the content", and
/// applying a content coding is exactly such a transformation
/// (RFC 9110 §7.7). A YARP gateway is an intermediary, so compressing past
/// the directive is a protocol violation — measured before this type existed:
/// an 8 KiB body sent under <c>no-transform</c> came back gzipped with the
/// directive intact.
/// </para>
/// <para>
/// <b>The request directive is honoured too, and it is a weaker thing.</b>
/// §5.2.1.6 says only that "the client is asking for intermediaries to avoid
/// transforming the content" — an ask, where the response form is an
/// obligation. Both are refused here because a caller who says so explicitly
/// should be believed and the check costs a header read; but the distinction
/// is written down rather than flattened, because this branch twice reached a
/// wrong answer by reasoning about the specification instead of quoting it.
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
        // Reading the REQUEST header makes the representation depend on it, so
        // it has to be advertised as a cache-selection dimension — otherwise a
        // shared cache serves a gzipped variant, stored for a caller who sent
        // no directive, to one who did, and the refusal below is bypassed by
        // something in front of this gateway. The base provider adds
        // Accept-Encoding on its own; this is the dimension this subclass
        // introduces and therefore the one it owes.
        //
        // Unconditionally, on every decision, because absence is a value: a
        // response compressed BECAUSE no directive arrived varies on the header
        // exactly as a refused one does. It costs cache efficiency — callers
        // send assorted Cache-Control values — and the alternative is a cache
        // that can undo the policy.
        //
        // Set here rather than at a purer seam because the interface has none:
        // ShouldCompressResponse is the one member the middleware calls, once,
        // before acting on the answer.
        AdvertiseVaryByCacheControl(context.Response);

        // Both directions, and they are not the same kind of rule. Checked
        // before the base call either way, so the directive wins outright
        // rather than depending on what else would have been decided.
        if (RefusesTransformation(context.Response.Headers.CacheControl) ||
            RefusesTransformation(context.Request.Headers.CacheControl))
        {
            return false;
        }

        return base.ShouldCompressResponse(context);
    }

    /// <summary>
    /// Adds <c>Cache-Control</c> to <c>Vary</c>, unless it is there already or
    /// the response has declared the wildcard.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A <c>Vary: *</c> from the destination is left exactly as it is.</b>
    /// The wildcard says the representation depends on something no field name
    /// captures, so no later request matches it; appending to that produces
    /// <c>*, Cache-Control</c>, which narrows nothing and reads as though a
    /// field list were meant.
    /// </para>
    /// <para>
    /// The presence check is the framework's own idiom — <c>ResponseCompressionBody</c>
    /// reads the comma-separated values before adding <c>Accept-Encoding</c>
    /// rather than appending blind — and the first version of this method did
    /// append blind, so a destination that already varied by
    /// <c>Cache-Control</c> got it twice.
    /// </para>
    /// </remarks>
    private static void AdvertiseVaryByCacheControl(HttpResponse response)
    {
        foreach (string? value in response.Headers.GetCommaSeparatedValues(HeaderNames.Vary))
        {
            if (value == "*" || string.Equals(value, HeaderNames.CacheControl, StringComparison.OrdinalIgnoreCase))
                return;
        }

        response.Headers.Append(HeaderNames.Vary, HeaderNames.CacheControl);
    }

    /// <summary>
    /// Whether a <c>Cache-Control</c> header carries <c>no-transform</c>.
    /// </summary>
    /// <remarks>
    /// <c>TryParse</c> returning false means the header says nothing this
    /// cares about, which is the same answer as its absence — a malformed
    /// <c>Cache-Control</c> is not a refusal.
    /// </remarks>
    private static bool RefusesTransformation(StringValues cacheControl) =>
        CacheControlHeaderValue.TryParse(cacheControl.ToString(), out CacheControlHeaderValue? parsed) &&
        parsed.NoTransform;
}
