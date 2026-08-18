using FluentValidation;
using Grpc.Core;
using Grpc.Core.Interceptors;

namespace Catalog.Api.Grpc;

/// <summary>
/// §10.5's 400 row, in gRPC's own vocabulary. <c>ValidationBehavior</c> throws
/// a <see cref="ValidationException"/> for a malformed request (§6.3), and over
/// HTTP <c>ValidationExceptionHandler</c> turns that into a 400 with a
/// field-keyed problem+json. Nothing was doing the equivalent here.
/// </summary>
/// <remarks>
/// <b>Its absence is a 500 in place of a 400.</b> Left untranslated the
/// exception reaches gRPC's own handler, which answers <c>Unknown</c> — and the
/// BFF's <c>UpstreamExceptionHandler</c> leaves anything it has not mapped as a
/// 500, so a caller's malformed query string comes back as this platform having
/// failed. The status is the whole point: <c>InvalidArgument</c> is the one
/// gRPC code that says "you sent the wrong thing".
/// <para>
/// It is <b>not</b> about retries, and an earlier version of this remark said
/// it was. <c>Unknown</c> travels as <c>grpc-status</c> on an HTTP 200 exactly
/// as every other gRPC outcome does, so the BFF's HTTP resilience pipeline
/// never sees it and retries nothing — measured in <c>UpstreamRetryTests</c>,
/// in the same change that wrote this file.
/// </para>
/// <para>
/// An interceptor rather than a <c>try</c> in the service, because the rule
/// belongs to every RPC this host ever adds — the same argument that puts
/// <c>ValidationExceptionHandler</c> in the pipeline rather than in an
/// endpoint.
/// </para>
/// </remarks>
internal sealed class ValidationInterceptor : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (ValidationException exception)
        {
            // Property name and message, joined — gRPC's status carries one
            // string where problem+json carries a keyed dictionary, so the key
            // goes into the text rather than being dropped. A caller debugging
            // "which field" is the whole audience for this.
            string detail = string.Join(
                "; ",
                exception.Errors.Select(failure => $"{failure.PropertyName}: {failure.ErrorMessage}"));

            throw new RpcException(new Status(StatusCode.InvalidArgument, detail));
        }
    }
}
