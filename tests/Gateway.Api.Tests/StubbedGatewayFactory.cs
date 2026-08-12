namespace Gateway.Api.Tests;

/// <summary>
/// The gateway with every cluster pointed at one <see cref="StubDestination"/>,
/// for the tests whose request has to reach the proxy.
/// </summary>
/// <remarks>
/// The active health check goes off with them. Left on, it would probe the
/// stub every ten seconds for the life of the host and — once the stub is
/// disposed — mark the cluster's only destination unhealthy, so a later
/// request would be rejected before the forwarder ran: a different status, for
/// a different reason, arriving on a timer.
/// </remarks>
public sealed class StubbedGatewayFactory(string destination) : GatewayFactory
{
    protected override IEnumerable<KeyValuePair<string, string>> AdditionalSettings =>
    [
        new("ReverseProxy:Clusters:catalog:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:catalog:HealthCheck:Active:Enabled", "false"),
        new("ReverseProxy:Clusters:ordering:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:inventory:Destinations:d1:Address", destination),
        new("ReverseProxy:Clusters:web-bff:Destinations:d1:Address", destination)
    ];
}
