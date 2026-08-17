using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// A real HTTP server standing in for the identity provider: a discovery
/// document and a token endpoint, and nothing else.
/// </summary>
/// <remarks>
/// A server rather than a substituted <see cref="HttpMessageHandler"/>, because
/// half of what is being tested is what <see cref="CachingTokenClient"/> does
/// with a <i>document</i> — that it reads <c>token_endpoint</c> out of the
/// discovery response rather than appending a Keycloak-shaped path to the
/// authority. A substituted handler would have to be told the answer, which is
/// the thing under test.
/// </remarks>
public sealed class StubIdentityProvider : IAsyncLifetime
{
    private WebApplication? _app;

    /// <summary>The authority, with the trailing slash a base address needs.</summary>
    public Uri Authority { get; private set; } = null!;

    /// <summary>How many times the discovery document has been fetched.</summary>
    public int Discoveries { get; private set; }

    /// <summary>Every token request's form fields, in order.</summary>
    public ConcurrentQueue<IReadOnlyDictionary<string, string>> TokenRequests { get; } = new();

    /// <summary>Seconds to declare each issued token valid for.</summary>
    public int? ExpiresIn { get; set; } = 300;

    /// <summary>Status to answer the token endpoint with, when not 200.</summary>
    public int TokenStatus { get; set; } = StatusCodes.Status200OK;

    /// <summary>The body to answer a non-200 token request with.</summary>
    public string TokenFailureBody { get; set; } = "{}";

    /// <summary>Answer the discovery document with no <c>token_endpoint</c>.</summary>
    public bool OmitTokenEndpoint { get; set; }

    public async ValueTask InitializeAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, 0));

        _app = builder.Build();

        // The realm path is part of the authority, exactly as Keycloak's is —
        // so a client that appended to a base address without a trailing slash
        // would ask for /.well-known/... at the root and miss this.
        _app.MapGet("/realms/test/.well-known/openid-configuration", () =>
        {
            Discoveries++;

            return OmitTokenEndpoint
                ? Results.Json(new { issuer = $"{Authority}" })
                : Results.Json(new { token_endpoint = $"{Authority}protocol/openid-connect/token" });
        });

        _app.MapPost("/realms/test/protocol/openid-connect/token", async (HttpContext context) =>
        {
            IFormCollection form = await context.Request.ReadFormAsync();
            TokenRequests.Enqueue(form.ToDictionary(f => f.Key, f => f.Value.ToString(), StringComparer.Ordinal));

            if (TokenStatus != StatusCodes.Status200OK)
                return Results.Content(TokenFailureBody, "application/json", statusCode: TokenStatus);

            Dictionary<string, object> body = new(StringComparer.Ordinal)
            {
                ["access_token"] = $"issued-{TokenRequests.Count}",
                ["token_type"] = "Bearer"
            };

            if (ExpiresIn is int lifetime)
                body["expires_in"] = lifetime;

            return Results.Json(body);
        });

        await _app.StartAsync();
        Authority = new Uri($"{_app.Urls.Single()}/realms/test/");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }
}
