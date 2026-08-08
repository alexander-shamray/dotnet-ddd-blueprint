using Catalog.Application;
using Catalog.Infrastructure;
using Common.Web;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Refuse to start if any registered service has a dependency the container
// cannot satisfy, or if a singleton captures a scoped one. Both are otherwise
// discovered on the first request that happens to need them.
builder.Host.UseDefaultServiceProvider(o =>
{
    o.ValidateOnBuild = true;
    o.ValidateScopes = true;
});

builder.AddCommonWebDefaults();                 // §13.2
builder.Services.AddCatalogApplication();       // §6.2
builder.Services.AddCatalogInfrastructure(builder.Configuration);   // §4.2, §7.1

// PR-07's OpenAPI deliverable (Appendix C): document only, no UI.
builder.Services.AddOpenApi();

WebApplication app = builder.Build();

// Middleware order is behaviour, not formatting (§4.2). Authentication and
// authorization join this pipeline at PR-16, endpoints at PR-10.
app.UseExceptionHandler();        // §10.5 — outermost, catching middleware faults
app.UseCorrelationId();           // §10.4 — above everything else that logs

app.MapCommonHealthEndpoints();   // §13.5 — anonymous; kubelet carries no token
app.MapOpenApi();

app.Run();

// Top-level statements compile to an INTERNAL Program, which
// WebApplicationFactory<Program> cannot see from another assembly (§12.4).
public partial class Program;
