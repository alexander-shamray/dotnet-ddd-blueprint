namespace Web.Bff.Tests;

/// <summary>
/// Locates a file in the repository by walking up from the test binary to
/// <c>Platform.slnx</c>.
/// </summary>
/// <remarks>
/// The third copy of this walk — <c>RealmImportTests</c> in
/// <c>Common.Web.Tests</c> made it first and <c>GrantablePermissionTests</c> in
/// <c>Gateway.Api.Tests</c> made it second — and the duplication is the same
/// trade those two took: the suites cannot reference one another (§4.3), and a
/// shared assembly for eight lines of directory walking would be a project
/// three hosts' test suites depend on.
/// <para>
/// What all three read is the same file, from three sides: the building block
/// that owns the audience constant, the host that owns a route's permission
/// name, and now the host that owns a client id.
/// </para>
/// </remarks>
public static class RepositoryFile
{
    /// <summary>The shipped Keycloak realm (§14.1).</summary>
    public const string RealmExport = "deploy/compose/keycloak/realm-export.json";

    /// <summary>The Compose file that deploys the BFF (§14.1).</summary>
    public const string ComposeFile = "deploy/compose/docker-compose.yml";

    public static string Locate(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Platform.slnx")))
            directory = directory.Parent;

        if (directory is null)
        {
            throw new InvalidOperationException(
                $"No Platform.slnx above '{AppContext.BaseDirectory}', so '{relativePath}' cannot be located.");
        }

        string path = Path.Combine(directory.FullName, relativePath);

        // An absent file must fail here rather than as an empty realm that
        // satisfies nothing and asserts nothing — a moved or renamed realm is
        // exactly the change these suites exist to catch.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}
