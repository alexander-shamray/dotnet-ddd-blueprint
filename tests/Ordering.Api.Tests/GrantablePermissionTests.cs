using System.Reflection;
using System.Text.Json;
using Shouldly;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// Every permission Ordering requires is one somebody can be granted.
/// </summary>
/// <remarks>
/// <para>
/// <b>PR-17 invented this test because it shipped without it and was wrong;
/// PR-18 is the first service PR that could repeat the mistake, and did.</b>
/// The two endpoint policies were registered and named while the realm's
/// <c>commerce-api</c> client held <c>catalog:write</c> and
/// <c>inventory:admin</c> — so every call to <c>/v1/orders</c> would have
/// answered 403 to every principal Keycloak can issue, permanently, on a
/// service whose whole point was that the gateway route stops answering 502.
/// §11.4 states the rule in both directions and this is the second one: a
/// permission something requires and the realm cannot grant is a path nobody
/// can reach.
/// </para>
/// <para>
/// <c>RealmImportTests</c> in <c>Common.Web.Tests</c> asserts the same realm's
/// role list is closed and could not catch this — it compares against literals
/// because that assembly is a building block and cannot reference a host to
/// read its constants. The check has to run from the side that owns the
/// constant, which is here.
/// </para>
/// <para>
/// <b><c>orders:admin</c> is deliberately outside this walk, and that is the
/// one difference from the gateway's copy.</b> It is a claim
/// <c>CancelOrderHandler</c> reads against a loaded aggregate rather than a
/// policy an endpoint names, so it is not in <c>OrderingPermissions</c> and
/// reflection cannot see it — which is correct, and would leave it unchecked.
/// The second test below covers it by name for that reason, and says why a
/// name-by-hand assertion is right there and wrong above.
/// </para>
/// </remarks>
public sealed class GrantablePermissionTests
{
    /// <summary>The client that owns the permission roles (§11.5).</summary>
    private const string ResourceClient = "commerce-api";

    [Fact]
    public void Every_permission_an_ordering_endpoint_requires_is_a_role_the_realm_can_grant()
    {
        // Read off the type, not listed by hand. Naming the two explicitly
        // would make this a second manual registry — a permission added to
        // OrderingPermissions and required by an endpoint would not enter the
        // assertion, which is the exact defect the test exists to prevent.
        string[] required =
        [
            .. typeof(OrderingPermissions)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string))
                .Select(f => (string)f.GetRawConstantValue()!)
        ];

        // The guard against the whole thing passing vacuously: a vocabulary
        // that emptied would satisfy every assertion below.
        required.ShouldNotBeEmpty();

        foreach (string permission in required)
        {
            Grantable().ShouldContain(
                permission,
                $"an Ordering endpoint requires '{permission}' (§11.4) and the realm's {ResourceClient} client " +
                "cannot grant it, so that path is 403 for every principal Keycloak can issue (§11.5)");
        }
    }

    [Fact]
    public void The_admin_claim_is_grantable_though_no_policy_names_it()
    {
        // Spelt by hand, where the test above refuses to be — and the reason
        // is the same rule read from the other end. orders:admin is not in
        // OrderingPermissions on purpose (§11.4: a claim is not a policy), so
        // there is no constant for reflection to find, and a walk over the
        // type would silently cover nothing. Without the role,
        // CancelOrderHandler's admin branch is unreachable code: no token this
        // realm can issue could carry the claim, and the one sanctioned
        // override of the ownership check would never fire in any environment.
        //
        // Grantable is the bar, not granted — neither development login holds
        // it, because the ownership 404 has to stay demonstrable with the
        // logins the realm ships.
        Grantable().ShouldContain("orders:admin");
    }

    private static string[] Grantable()
    {
        using JsonDocument realm = JsonDocument.Parse(
            File.ReadAllText(RepositoryFile("deploy/compose/keycloak/realm-export.json")));

        return
        [
            .. realm.RootElement
                .GetProperty("roles")
                .GetProperty("client")
                .GetProperty(ResourceClient)
                .EnumerateArray()
                .Select(r => r.GetProperty("name").GetString())
                .OfType<string>()
        ];
    }

    /// <summary>
    /// The same walk <c>RealmImportTests</c> makes, and for the same reason: a
    /// test asserting a repository file has to find it from a bin directory
    /// whose depth is a build detail.
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
        // satisfies nothing and asserts nothing.
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"'{relativePath}' is not in the repository at '{directory.FullName}'.",
                path);
    }
}
