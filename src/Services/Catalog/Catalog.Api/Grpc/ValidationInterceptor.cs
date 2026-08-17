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
/// <b>Its absence is a 500, and a 500 is a retryable status.</b> Left
/// untranslated the exception reaches gRPC's own handler, which answers
/// <c>Unknown</c> — and the BFF's resilience pipeline (§9.7) treats that as a
/// transient fault and spends all three attempts on a request that was
/// malformed the first time. So this is not cosmetic parity with the HTTP side:
/// it is what stops a client's bad input from being amplified into three of
/// Catalog's database round trips.
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
