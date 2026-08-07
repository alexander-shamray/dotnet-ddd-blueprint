using System.Reflection;

namespace Common.Web;

/// <summary>
/// The version stamped onto the OpenTelemetry resource as
/// <c>service.version</c> (§13.2), so a trace or a metric says which build
/// produced it.
/// </summary>
public static class BuildInfo
{
    /// <summary>
    /// The entry assembly's informational version, without the source-revision
    /// suffix, or <c>0.0.0</c> when there is no entry assembly to ask.
    /// </summary>
    public static string Version { get; } = Read();

    private static string Read()
    {
        // The ENTRY assembly, not this one: the version that matters is the
        // host's, and Common.Web is a library every host references (§4.1).
        string? informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
            return "0.0.0";

        // Deterministic builds append "+<sha>" (SourceRevisionId). It is
        // dropped rather than kept: service.version is grouped on, and a value
        // that changes every commit turns one series into thousands.
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
