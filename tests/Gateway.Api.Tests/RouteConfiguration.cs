using Microsoft.Extensions.Configuration;

namespace Gateway.Api.Tests;

/// <summary>
/// One route of <c>ReverseProxy:Routes</c>, read from the host's own
/// configuration rather than from the file on disk.
/// </summary>
/// <remarks>
/// Through <see cref="IConfiguration"/> because that is the exact text YARP
/// binds: reading <c>appsettings.json</c> with <c>System.Text.Json</c> would
/// need the comment handling the configuration provider applies, and would
/// then be asserting against a second reader rather than against the one whose
/// answer decides the routing.
/// </remarks>
internal sealed record RouteConfiguration(
    string Id,
    string ClusterId,
    string MatchPath,
    string? AuthorizationPolicy,
    string? RateLimiterPolicy,
    IReadOnlyList<string> RemovedPrefixes)
{
    public static IReadOnlyList<RouteConfiguration> ReadAll(IConfiguration configuration) =>
        [.. configuration.GetSection("ReverseProxy:Routes").GetChildren().Select(Read)];

    /// <summary>
    /// The path a destination actually receives: the match with its prefix
    /// stripped, truncated at the catch-all. <c>/api/v1/catalog/{**catch-all}</c>
    /// stripped of <c>/api</c> is <c>/v1/catalog</c>.
    /// </summary>
    public string ForwardedPathPrefix
    {
        get
        {
            string stripped = MatchPath;

            foreach (string prefix in RemovedPrefixes)
            {
                if (stripped.StartsWith(prefix, StringComparison.Ordinal))
                    stripped = stripped[prefix.Length..];
            }

            int catchAll = stripped.IndexOf("/{", StringComparison.Ordinal);
            string literal = catchAll < 0 ? stripped : stripped[..catchAll];

            return literal.Length == 0 ? "/" : literal;
        }
    }

    /// <summary>
    /// The namespace the external path sits under — <c>/api</c> or <c>/bff</c>.
    /// One strip per namespace is §10.2's rule, and this is the half of it the
    /// route file states.
    /// </summary>
    public string Namespace
    {
        get
        {
            int second = MatchPath.IndexOf('/', 1);

            return second < 0 ? MatchPath : MatchPath[..second];
        }
    }

    private static RouteConfiguration Read(IConfigurationSection route)
    {
        // Transforms is an array of single-entry objects, so each child's own
        // children are the transform's key and value. Only PathRemovePrefix
        // matters here; the header transform beside it in catalog-public is
        // not path composition.
        List<string> removed =
        [
            .. route
                .GetSection("Transforms")
                .GetChildren()
                .Select(t => t["PathRemovePrefix"])
                .Where(p => !string.IsNullOrEmpty(p))
                .Select(p => p!)
        ];

        return new RouteConfiguration(
            route.Key,
            route["ClusterId"] ?? string.Empty,
            route["Match:Path"] ?? string.Empty,
            route["AuthorizationPolicy"],
            route["RateLimiterPolicy"],
            removed);
    }
}
