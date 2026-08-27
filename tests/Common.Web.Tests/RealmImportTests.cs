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
    /// realm that mints a token a person uses. The realm holds nine: this one,
    /// the <c>commerce-api</c> resource client that owns the permission roles,
    /// PR-19's <c>web-bff</c> service account, and Keycloak's own six.
    /// Assertions about "a usable token" are about this client's.
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

    /// <summary>
    /// Keycloak speaks more than one protocol, and every assertion in this file
    /// is about a JWT. A client, a scope or a mapper switched to <c>saml</c>
    /// keeps its name, its flags and its config — so every other test here
    /// stays green while the thing they describe stops being an OIDC token.
    /// </summary>
    private const string Protocol = "openid-connect";

    [Fact]
    public void Every_part_of_the_token_path_speaks_openid_connect()
    {
        // The client the README logs in through, the scope that carries the
        // audience and the permissions, and the two mappers that write them.
        // Nothing else in this suite reads `protocol` at all, so this is the
        // one assertion standing between a realm that issues JWTs and one that
        // issues something no part of this platform can validate.
        JsonElement tokenClient = Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient.GetProperty("protocol").GetString().ShouldBe(Protocol);
        CommerceApiScope.GetProperty("protocol").GetString().ShouldBe(Protocol);

        foreach (JsonElement mapper in MappersOf(CommerceApiScope))
        {
            mapper.GetProperty("protocol").GetString().ShouldBe(
                Protocol,
                $"'{mapper.GetProperty("name").GetString()}' writes into a token this platform reads");
        }
    }

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
            .ShouldContain(
                Audience,
                $"'{TokenClient}' does not hold {Audience} as a default scope, so its " +
                "tokens carry no audience this platform accepts");
    }

    [Fact]
    public void The_documented_login_can_actually_be_performed()
    {
        // The audience assertion above says the token would be usable; this
        // says one can be obtained at all. The compose README's recipe is a
        // password grant against web-app, which needs the direct access grant
        // enabled and a public client — turn either off and every other test
        // in this file stays green while the documented flow returns 401 from
        // Keycloak before the platform is even reached.
        JsonElement tokenClient = Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient.GetProperty("enabled").GetBoolean()
            .ShouldBeTrue($"a disabled '{TokenClient}' satisfies both flags below and issues nothing");
        tokenClient.GetProperty("directAccessGrantsEnabled").GetBoolean()
            .ShouldBeTrue($"the README obtains a token by password grant against '{TokenClient}'");
        tokenClient.GetProperty("publicClient").GetBoolean()
            .ShouldBeTrue($"the README's grant sends no secret, and none is committed for '{TokenClient}'");
    }

    [Fact]
    public void The_access_token_lifetime_is_the_one_the_chapter_states()
    {
        // §11.3 states the lifetime normatively, and this file is what makes
        // it a fact rather than a preference: Common.Web sets no lifetime at
        // all — its AddJwtBearer validates the `exp` Keycloak wrote — so the
        // chapter's number and the realm's are two statements with nothing
        // between them. Hence a literal here and no constant in Common.Web: a
        // constant nothing reads would be a registration standing in for a
        // control, which is the shape ADR-033 was written to withdraw.
        //
        // This is most of the exposure and not a tuning knob. There is no
        // denylist consumer and no introspection call (ADR-033), so a token
        // stolen, or a user disabled at Keycloak, keeps working for up to its
        // remaining lifetime — anywhere from nearly zero to the whole of it,
        // which is a bound rather than a duration. Lengthen it here and
        // §11.3's stated window is silently wrong everywhere it is quoted.
        //
        // Most, not all: a lifetime check accepts a token until `exp` PLUS
        // AuthenticationExtensions' 30-second ClockSkew, so ADR-033's
        // revocation bound is 330 seconds and this value is the larger of the
        // two terms rather than the whole sum. The skew is pinned where it is
        // set — JwtAuthenticationTests asserts ClockSkew is thirty seconds —
        // so asserting it here as well would put one number in two suites and
        // give it two places to drift from.
        const int statedLifetimeSeconds = 300;

        Root.GetProperty("accessTokenLifespan").GetInt32().ShouldBe(
            statedLifetimeSeconds,
            "§11.3 states the access-token lifetime and this realm is what sets it");

        // The realm carries a SECOND lifetime — accessTokenLifespanForImplicitFlow,
        // 900 — and the assertion above says nothing about it. It is unreachable
        // only because no client enables the implicit flow, which is a premise
        // §11.3's stated window rests on and which nothing else here checks.
        // Enabling implicit flow on one client would triple the exposure with
        // every number in this file still reading 300, so the premise is
        // asserted rather than assumed.
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            client.GetProperty("implicitFlowEnabled").GetBoolean().ShouldBeFalse(
                $"'{client.GetProperty("clientId").GetString()}' enables the implicit flow, " +
                "whose tokens live for accessTokenLifespanForImplicitFlow and not for the " +
                "lifetime §11.3 states");
        }
    }

    [Fact]
    public void The_browser_is_issued_no_refresh_token()
    {
        // §11.2's flow ends at the browser, so anything web-app is issued is
        // reachable by any script on the origin. A refresh token there turns
        // one XSS into account takeover that outlives the session and survives
        // a password change; with none issued, the exposure is bounded by the
        // access-token lifetime pinned above.
        //
        // Measured both ways against Keycloak 26.0 — the version §14.1 pins —
        // with this realm and the `demo` login: without this attribute the
        // token response carries a `refresh_token` and `refresh_expires_in`
        // 1800, with it there is no `refresh_token` key at all and
        // `refresh_expires_in` is 0. Nothing in the solution compiles
        // differently either way, which is what earns it a test rather than a
        // paragraph.
        JsonElement tokenClient = Root
            .GetProperty("clients")
            .EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == TokenClient);

        tokenClient
            .GetProperty("attributes")
            .GetProperty("use.refresh.tokens")
            .GetString()
            .ShouldBe(
                "false",
                $"'{TokenClient}' is the browser's client, and Keycloak's default is to issue it " +
                "a refresh token");
    }

    [Fact]
    public void Both_development_logins_hold_exactly_what_the_readme_says()
    {
        // The two halves of §11.5's demonstration, and the negative one is the
        // one worth a test: `browser` proving a refusal is only a proof while
        // it holds nothing. Granting it catalog:write — the obvious "fix" for
        // a 403 somebody did not expect — turns the 403 case into a second
        // success case, and nothing else here would notice.
        JsonElement users = Root.GetProperty("users");

        string[] Permissions(string username) =>
        [
            .. users.EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == username)
                .GetProperty("clientRoles")
                .GetProperty(Audience)
                .EnumerateArray()
                .Select(r => r.GetString())
                .OfType<string>()
        ];

        // demo gains Ordering's two endpoint permissions with PR-18, so the
        // inner loop the compose README documents actually works — the
        // catalog:write parallel, one service over. It does NOT gain
        // orders:admin: that role is grantable and held by nobody, so the
        // ownership 404 stays demonstrable with the logins this realm ships.
        Permissions("demo").ShouldBe(
            ["catalog:write", "orders:write", "orders:cancel"],
            ignoreOrder: true);

        JsonElement browser = users.EnumerateArray()
            .Single(u => u.GetProperty("username").GetString() == "browser");

        browser.TryGetProperty("clientRoles", out JsonElement granted)
            .ShouldBeFalse("'browser' exists to prove a refusal, so it must hold no client role at all");

        // And that both can log in at all, with the password the README prints.
        // A user disabled, a credential Keycloak marks temporary — which forces
        // a password reset the README's non-interactive grant cannot perform —
        // or simply a different password: each fails the documented commands
        // with a 401 while every role assertion above stays green. The value is
        // pinned rather than merely present, because §11.6's carve-out is for
        // *documented* local defaults, and one nobody can guess is not one.
        foreach (string username in (string[])["demo", "browser"])
        {
            JsonElement user = users.EnumerateArray()
                .Single(u => u.GetProperty("username").GetString() == username);

            user.GetProperty("enabled").GetBoolean()
                .ShouldBeTrue($"'{username}' is one of §11.5's two documented logins");

            JsonElement password = user.GetProperty("credentials").EnumerateArray()
                .Single(c => c.GetProperty("type").GetString() == "password");

            password.GetProperty("value").GetString()
                .ShouldBe(username, $"the compose README documents '{username}' as its own password");

            password.GetProperty("temporary").GetBoolean()
                .ShouldBeFalse($"a temporary credential makes '{username}' unusable by the README's password grant");
        }
    }

    [Fact]
    public void No_role_description_exceeds_what_keycloak_can_store()
    {
        // Keycloak's ROLE.DESCRIPTION is VARCHAR(255), and an over-long value
        // does not truncate — the import throws, the container exits 1, and
        // `up --wait` fails with "dependency failed to start" naming Keycloak
        // and nothing about the column. PR-18 shipped a 380-character
        // description and the compose smoke was the only thing that noticed,
        // three review rounds after the realm was edited.
        //
        // This file is prose-heavy by house style, which is exactly why the
        // limit needs a test rather than a habit: the reasoning belongs in the
        // configuration and the tests that read it, and the realm gets the
        // sentence that fits.
        const int keycloakDescriptionLimit = 255;

        (string Name, string Description)[] roles =
        [
            .. Root.GetProperty("roles").GetProperty("client").GetProperty(Audience).EnumerateArray()
                .Select(r => (
                    Name: r.GetProperty("name").GetString()!,
                    Description: r.TryGetProperty("description", out JsonElement d) ? d.GetString()! : ""))
        ];

        roles.ShouldNotBeEmpty();

        foreach ((string name, string description) in roles)
        {
            description.Length.ShouldBeLessThanOrEqualTo(
                keycloakDescriptionLimit,
                $"'{name}' has a {description.Length}-character description; Keycloak stores 255 and " +
                "the import fails the whole realm rather than truncating");
        }
    }

    [Fact]
    public void The_permission_vocabulary_is_a_closed_set_of_client_roles()
    {
        // The permissions a policy can require have to exist somewhere a person
        // can grant them. catalog:write is Catalog's one policy (§11.4);
        // inventory:admin is the gateway's, and it is here for a reason worth
        // knowing: the permission a ROUTE requires (§10.2) obeys the same rule
        // as one an endpoint requires, and PR-17 registered the policy without
        // the role — so /api/v1/inventory was 403 for every principal this
        // realm can issue, permanently. Grantable is the bar, not granted:
        // neither development login holds it, because the route it guards has
        // no service behind it yet.
        string[] roles =
        [
            .. Root.GetProperty("roles").GetProperty("client").GetProperty(Audience).EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];

        // The whole set, not a containment check — the test is named for a
        // closed vocabulary and ShouldContain would permit any number of
        // undeclared permissions to be grantable in Keycloak. A service's
        // permissions join this list in the PR that registers the policy
        // requiring them, which is the same rule §11.4 states for the
        // constants.
        // Ordering's three joined with PR-18. Two are policies its endpoints
        // require; orders:admin is a claim CancelOrderHandler reads and no
        // endpoint names, and it is here on inventory:admin's terms — without
        // the role, no token this realm can issue could carry the claim, and
        // the handler's admin branch would be unreachable code rather than an
        // override somebody can be granted.
        roles.ShouldBe(
            [
                "catalog:write",
                "inventory:admin",
                "orders:write",
                "orders:cancel",
                "orders:admin"
            ],
            ignoreOrder: true);
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
        string[] builtins = ["basic", "profile", "email", "roles", "web-origins", "acr"];
        string[] names = [.. ClientScopes.Select(s => s.GetProperty("name").GetString()).OfType<string>()];

        foreach (string builtin in builtins)
        {
            names.ShouldContain(
                builtin,
                $"'{builtin}' is a Keycloak built-in; a realm that declares clientScopes " +
                "and omits it never creates it");
        }

        // Declared is not assigned, and the gap between them is the same defect
        // by a shorter route: dropping `basic` from web-app's defaultClientScopes
        // takes `sub` out of the README's token while the scope itself still
        // exists in the realm and every assertion above stays green.
        string[] assigned =
        [
            .. Root.GetProperty("clients").EnumerateArray()
                .Single(c => c.GetProperty("clientId").GetString() == TokenClient)
                .GetProperty("defaultClientScopes").EnumerateArray()
                .Select(s => s.GetString())
                .OfType<string>()
        ];

        foreach (string builtin in builtins)
        {
            assigned.ShouldContain(
                builtin,
                $"'{TokenClient}' does not receive '{builtin}', so its tokens are missing " +
                "what that scope carries");
        }

        // And present and assigned is still not carrying: `basic` matters only
        // because of the mapper inside it. Deleting that mapper, or turning off
        // its access.token.claim, leaves the scope declared and assigned while
        // every token loses `sub` — which is the exact failure the whole test
        // is named for, reached by the one route the two checks above do not
        // cover. ICurrentUser.Id reads that claim and would throw on every
        // authenticated request in the platform.
        JsonElement basic = ClientScopes.Single(s => s.GetProperty("name").GetString() == "basic");

        JsonElement subject = MappersOf(basic).Single(
            m => m.GetProperty("protocolMapper").GetString() == "oidc-sub-mapper");

        subject.GetProperty("config").GetProperty("access.token.claim").GetString()
            .ShouldBe("true", "a `sub` on the id token alone is invisible to a bearer check");
    }

    /// <summary>
    /// The one client whose grant requires both sides to agree on a secret
    /// (§11.5), and the documented local-development value it agrees on.
    /// </summary>
    /// <remarks>
    /// PR-19's carve-out, and the premise it falsified is worth naming: this
    /// test used to say <i>no</i> client ships a secret, which was true while
    /// no client used the client-credentials grant. The BFF is the first that
    /// does, and a client-credentials flow is precisely two parties holding
    /// the same string — one of which is a committed Compose file. Letting
    /// Keycloak generate the secret would leave the realm and the deployment
    /// disagreeing, and the BFF refused at the token endpoint on every call.
    /// <para>
    /// So the rule narrows rather than lapses, and narrowing makes it
    /// stronger: the value is pinned, so a randomly generated secret — a
    /// credential nobody chose, that looks real enough to be reused where it
    /// would matter — still fails here, and so does a real one.
    /// </para>
    /// </remarks>
    private const string CredentialClient = "web-bff";
    private const string DocumentedLocalSecret = "local-dev-secret";

    [Fact]
    public void No_client_ships_a_secret_but_the_one_whose_grant_needs_one()
    {
        foreach (JsonElement client in Root.GetProperty("clients").EnumerateArray())
        {
            string clientId = client.GetProperty("clientId").GetString()!;
            bool ships = client.TryGetProperty("secret", out JsonElement secret);

            if (clientId != CredentialClient)
            {
                // §11.6 and the local-development carve-out: Compose's
                // documented defaults are deliberate, and a randomly generated
                // secret is not one of them. Keycloak regenerates on import.
                ships.ShouldBeFalse($"'{clientId}' ships a secret and needs none");

                continue;
            }

            ships.ShouldBeTrue(
                $"'{clientId}' authenticates with the client-credentials grant, so the realm and " +
                "the deployment have to hold the same value (§11.5)");

            // The documented default and nothing else. The matching half lives
            // in deploy/compose/docker-compose.yml as
            // ${BFF_CLIENT_SECRET:-local-dev-secret}, and Web.Bff.Tests'
            // RealmClientTests asserts the two files agree — which is the
            // assertion this one cannot make, being a building block's suite
            // that may not read a host's deployment.
            secret.GetString().ShouldBe(
                DocumentedLocalSecret,
                "a secret in a committed realm must be the documented local default, " +
                "never a generated or real one (§11.6)");
        }
    }

    [Fact]
    public void The_resource_client_can_mint_no_token_of_its_own()
    {
        // Why the absent secret above is safe rather than merely tidy: Keycloak
        // generates one on import, so `commerce-api` has a working credential
        // in every running realm. What makes that harmless is that it has no
        // flow to spend it on — the client exists to own the permission
        // vocabulary and to name an audience, and nothing else.
        //
        // Enable any one of these four and the regenerated secret becomes a
        // way to obtain tokens carrying every permission in the platform, with
        // the secret test above still green because the file still ships none.
        JsonElement resource = Root.GetProperty("clients").EnumerateArray()
            .Single(c => c.GetProperty("clientId").GetString() == Audience);

        foreach (string flow in (string[])
        [
            "standardFlowEnabled",
            "implicitFlowEnabled",
            "directAccessGrantsEnabled",
            "serviceAccountsEnabled"
        ])
        {
            resource.GetProperty(flow).GetBoolean().ShouldBeFalse(
                $"'{Audience}' owns the permission vocabulary; '{flow}' would let it mint tokens too");
        }
    }

    [Fact]
    public void No_realm_role_grants_a_permission_by_composition()
    {
        // `browser` proving a refusal rests on it holding no permission, and
        // the test above checks the direct grant only. Every user also holds
        // `default-roles-commerce`, which is a composite — so a permission
        // added to that composite, or to any realm role it includes, reaches
        // the token through the same client-role mapper while `browser` still
        // has no `clientRoles` property of its own and every other assertion
        // here stays green. The documented 403 becomes a 200 and nothing says
        // so.
        //
        // This is the realm-role hazard §11.5 already names from the other
        // direction: a realm-role mapper would have put Keycloak's internals
        // into the permission claim. Composition is the same leak by
        // inheritance rather than by mapper.
        foreach (JsonElement role in Root.GetProperty("roles").GetProperty("realm").EnumerateArray())
        {
            if (!role.TryGetProperty("composites", out JsonElement composites) ||
                !composites.TryGetProperty("client", out JsonElement clients))
            {
                continue;
            }

            clients.TryGetProperty(Audience, out JsonElement granted).ShouldBeFalse(
                $"realm role '{role.GetProperty("name").GetString()}' composes a '{Audience}' role, " +
                "so every user holding it carries that permission");
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
        {
            throw new InvalidOperationException(
                $"No Platform.slnx above '{AppContext.BaseDirectory}', so '{relativePath}' cannot be located.");
        }

        string path = Path.Combine(directory.FullName, relativePath);

        // An absent file must fail here rather than as an empty realm that
        // satisfies nothing and asserts nothing — a moved or renamed realm is
        // exactly the change this suite exists to catch.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}
