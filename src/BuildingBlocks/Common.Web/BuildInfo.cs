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
    public static string Version { get; } = Normalise(Read());

    /// <summary>
    /// Strips the source-revision suffix and supplies the fallback. Public and
    /// separate from <see cref="Version"/> for one reason: a test cannot choose
    /// what the entry assembly is stamped with, so a test against
    /// <c>Version</c> alone passes against a method that returns a constant.
    /// This takes its input as an argument, so the parsing can be asserted.
    /// </summary>
    public static string Normalise(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
            return "0.0.0";

        // Deterministic builds append "+<sha>" (SourceRevisionId). It is
        // dropped rather than kept: service.version is a dimension people
        // group by, and a value that changes every commit turns one series
        // into thousands.
        int plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informationalVersion : informationalVersion[..plus];
    }

    // The ENTRY assembly, not this one: the version that matters is the host's,
    // and Common.Web is a library every host references (§4.1).
    private static string? Read() =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
}
