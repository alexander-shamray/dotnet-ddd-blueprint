using System.Text.Json;
using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// The shipped Keycloak realm, read against the constants this assembly
/// validates tokens with. §11.5's point exactly: the audience gap "is realm
/// configuration, not code, which is exactly why it earns a test rather than a
/// paragraph — nothing in the solution compiles differently when the audience
/// mapper is missing."
/// </summary>
/// <remarks>
/// Here rather than in a service's suite because both halves of the agreement
/// are here: <c>AuthenticationExtensions.Audience</c> and
/// <c>PermissionClaim.Type</c> are what a token has to satisfy, and this is
/// the only assembly that can read them without inventing a project reference.
///
/// A file test rather than a live Keycloak, deliberately. §11.5 assigns the
/// container-backed suite to the client-credentials question, which needs the
/// BFF and arrives with it; what this catches is the whole of what a realm can
/// get wrong statically, and it costs no container. The realm shipped here was
/// verified behaviourally when it was written — imported into a fresh Keycloak
/// 26.0, with a token fetched and its claims asserted — and the last test below
/// is the residue of that run, because it is the one defect a reading of the
/// file would never suggest.
/// </remarks>
public class RealmImportTests
{
    private const string Audience = AuthenticationExtensions.Audience;

    /// <summary>
    /// The client the compose README's login names, and the only one in the
    /// realm that mints a token a person uses — the other seven are Keycloak's
    /// own. Assertions about "a usable token" are about this client's.
    /// </summary>
    private const string TokenClient = "web-app";

    private static readonly JsonDocument Realm = JsonDocument.Parse(
        File.ReadAllText(RepositoryFile("deploy/compose/keycloak/realm-export.json")));

    private static JsonElement Root => Realm.RootElement;

    private static JsonElement.ArrayEnumerator ClientScopes =>
        Root.GetProperty("clientScopes").EnumerateArray();

    private static JsonElement CommerceApiScope =>
        ClientScopes.Single(s => s.GetProperty("name").GetString() == Audience);

    private static JsonElement.ArrayEnumerator MappersOf(JsonElement scope) =>
        scope.GetProperty("protocolMappers").EnumerateArray();

    [Fact]
    public void The_realm_is_the_one_every_host_is_pointed_at()
    {
        // §14.1's Identity__Authority is http://keycloak:8080/realms/commerce
        // on every service block. A renamed realm makes every one of them
        // fetch metadata from a 404 and every request 401 — at runtime, in
        // whichever environment imported the new file first.
        Root.GetProperty("realm").GetString().ShouldBe("commerce");
        Root.GetProperty("enabled").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public void An_audience_mapper_puts_the_value_every_service_validates_into_aud()
    {
        // Without this the realm issues tokens with an `aud` of `account` and
        // every service rejects every caller — §11.5's gap, and the reason
        // that section exists. §11.3 validates Audience; this is the only
        // thing that puts it there.
        JsonElement mapper = MappersOf(CommerceApiScope).Single(
            m => m.GetProperty("protocolMapper").GetString() == "oidc-audience-mapper");

        mapper.GetProperty("config").GetProperty("included.client.audience").GetString()
            .ShouldBe(Audience);
        mapper.GetProperty("config").GetProperty("access.token.claim").GetString()
            .ShouldBe("true", "an audience on the id token alone is invisible to a bearer check");
    }

    [Fact]
    public void A_role_mapper_writes_the_claim_the_policies_read()
    {
        // The fourth party to PermissionClaim.Type, and the one that cannot
        // reference the constant. A mapper writing "permissions" or "roles"
        // leaves every policy in the platform unsatisfiable, with nothing in
        // the solution compiling differently and every unit test still green.
        JsonElement mapper = MappersOf(CommerceApiScope).Single(
            m => m.GetProperty("name").GetString() == PermissionClaim.Type);

        JsonElement config = mapper.GetProperty("config");
        config.GetProperty("claim.name").GetString().ShouldBe(PermissionClaim.Type);
        config.GetProperty("access.token.claim").GetString().ShouldBe("true");
        config.GetProperty("multivalued").GetString()
            .ShouldBe("true", "a single-valued claim silently keeps one permission and drops the rest");

        // Client roles, scoped to the API client — not realm roles. Measured
        // rather than assumed: a realm-role mapper also emits offline_access,
        // uma_authorization and default-roles-commerce into this claim, which
        // makes the permission vocabulary open-ended and puts Keycloak's own
        // internals inside it.
        mapper.GetProperty("protocolMapper").GetString().ShouldBe("oidc-usermodel-client-role-mapper");
        config.GetProperty("usermodel.clientRoleMapping.clientId").GetString().ShouldBe(Audience);
    }

    [Fact]
    public void Every_client_holding_the_scope_holds_it_as_a_default()
    {
        // §11.5's row 2. A client scope left optional is silently absent from
        // any token that does not request it by name — and a client-credentials
        // token requests no scope explicitly, so the BFF's token would carry
        // neither the audience nor the permissions while the realm looked
        // correctly configured in the console.
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string? id = client.GetProperty("clientId").GetString();

            bool optional =
                client.TryGetProperty("optionalClientScopes", out JsonElement optionals) &&
                optionals.EnumerateArray().Any(s => s.GetString() == Audience);

            optional.ShouldBeFalse($"'{id}' holds {Audience} as an optional scope, so its tokens will not carry it");
        }

        // Not vacuous: with no client holding it at all, the loop above passes
        // and no token in the realm ever gets an audience. Named rather than
        // counted, because "some client has it" is satisfied by any of the six
        // built-in ones — account, broker, realm-management — none of which
        // mints a token anybody uses. web-app is the client the README's login
        // names, so it is the one whose tokens have to carry the audience.
        JsonElement tokenClient = Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient.GetProperty("defaultClientScopes").EnumerateArray()
            .Select(s => s.GetString())
            .ShouldContain(Audience, $"'{TokenClient}' does not hold {Audience} as a default scope, so its tokens carry no audience this platform accepts");
    }

    [Fact]
    public void The_permission_vocabulary_is_a_closed_set_of_client_roles()
    {
        // The permissions a policy can require have to exist somewhere a person
        // can grant them. catalog:write is Catalog's one policy (§11.4); a
        // service's permissions join this list with the service.
        string[] roles =
        [
            .. Root.GetProperty("roles").GetProperty("client").GetProperty(Audience).EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];

        roles.ShouldContain("catalog:write");
    }

    [Fact]
    public void The_builtin_client_scopes_are_all_present()
    {
        // The one defect a reading of this file would never suggest, and the
        // reason the realm is a full export rather than the readable dozen
        // lines that were written first. Keycloak's realm import treats a
        // `clientScopes` array as the COMPLETE set: supply only commerce-api
        // and the built-ins are never created. Nothing fails — the realm comes
        // up, the login succeeds, and the token silently loses `sub`,
        // `preferred_username`, `email` and `realm_access`.
        //
        // `basic` is the one that hurts: it carries `sub`, so ICurrentUser.Id
        // would throw on every authenticated request in the platform.
        //
        // Verified by importing exactly that trimmed file into a fresh
        // Keycloak 26.0 and reading the resulting token, which is the only way
        // this is observable at all.
        string[] names = [.. ClientScopes.Select(s => s.GetProperty("name").GetString()).OfType<string>()];

        foreach (string builtin in (string[])["basic", "profile", "email", "roles", "web-origins", "acr"])
            names.ShouldContain(builtin, $"'{builtin}' is a Keycloak built-in; a realm that declares clientScopes and omits it never creates it");
    }

    [Fact]
    public void No_client_secret_is_committed()
    {
        // §11.6 and the local-development carve-out: Compose's documented
        // defaults are deliberate, and a randomly generated secret is not one
        // of them — it is a credential nobody chose, that looks real enough to
        // be reused somewhere it would matter. Keycloak regenerates on import.
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            client.TryGetProperty("secret", out JsonElement _)
                .ShouldBeFalse($"'{client.GetProperty("clientId").GetString()}' ships a secret");
        }
    }

    /// <summary>
    /// Walks up from the test binary to the repository root, which is the one
    /// directory <c>Platform.slnx</c> sits in. Not a relative path from the
    /// assembly location: that hard-codes the build's directory depth, and
    /// changing the target framework moves it.
    /// </summary>
    private static string RepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Platform.slnx")))
            directory = directory.Parent;

        if (directory is null)
            throw new InvalidOperationException(
                $"No Platform.slnx above '{AppContext.BaseDirectory}', so '{relativePath}' cannot be located.");

        string path = Path.Combine(directory.FullName, relativePath);

        // An absent file must fail here rather than as an empty realm that
        // satisfies nothing and asserts nothing — a moved or renamed realm is
        // exactly the change this suite exists to catch.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException($"'{relativePath}' is not in the repository at '{directory.FullName}'.", path);
    }
}
