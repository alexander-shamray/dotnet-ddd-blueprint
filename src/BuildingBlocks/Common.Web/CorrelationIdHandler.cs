using Microsoft.AspNetCore.Http;

namespace Common.Web;

/// <summary>
/// §10.4's outbound half: copies this request's correlation ID onto a call
/// this host makes to a peer.
/// </summary>
/// <remarks>
/// <b>§10.4 promises the ID "propagates through every service", and until this
/// existed the middleware only delivered half of that.</b>
/// <see cref="CorrelationIdExtensions.UseCorrelationId"/> reads or mints the
/// ID and writes it onto the inbound request and the outbound response —
/// everything a host needs to log consistently, and nothing that leaves the
/// process. The asynchronous path was never affected: §9.1's envelope carries
/// <c>CorrelationId</c> as a member, so a message takes it along by
/// construction. The synchronous path had nothing, so the callee saw no header
/// and minted its own ID, and one incident spanned two of them.
/// <para>
/// It lives here rather than in the one host that calls a peer (§9.7,
/// ADR-017) because the guarantee is §10.4's and not the BFF's. Splitting the
/// two halves across projects would put the promise in a chapter, the inbound
/// half in this file and the outbound half somewhere with no reason to keep
/// them in step — and <see cref="CorrelationIdExtensions.Header"/> is here in
/// any case. It is registered by the host that needs it rather than by
/// <c>AddCommonWebDefaults</c>, because a <c>DelegatingHandler</c> attaches to
/// a named client and a host with no outbound client has none to attach it to.
/// </para>
/// <para>
/// <b>What it does not do is invent one.</b> Absent an inbound ID this sends
/// no header at all, and the callee's own middleware mints one from the
/// current trace — which is the right answer for a call with no request behind
/// it, such as a background job. Sending an empty header instead would defeat
/// the blank-is-missing guard on the other side.
/// </para>
/// </remarks>
public sealed class CorrelationIdHandler(IHttpContextAccessor context) : DelegatingHandler
{
    // cancellationToken rather than this repository's usual ct: CA1725 requires
    // an override to keep the base declaration's parameter name, and ADR-019
    // makes that an error. The same correction ClientCredentialsHandler
    // carries.
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        string? correlationId = context.HttpContext?.Request
            .Headers[CorrelationIdExtensions.Header]
            .FirstOrDefault();

        // Set rather than added: a retried attempt runs this handler again on
        // the same HttpRequestMessage, and Add would accumulate one value per
        // attempt into a header the callee reads with FirstOrDefault.
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.Remove(CorrelationIdExtensions.Header);
            request.Headers.Add(CorrelationIdExtensions.Header, correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
