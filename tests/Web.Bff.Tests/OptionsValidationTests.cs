using Common.Web;
using Microsoft.Extensions.Options;
using Shouldly;
using Web.Bff.Identity;
using Xunit;

namespace Web.Bff.Tests;

/// <summary>
/// §15.4's <c>ValidateOnStart</c>, from the side that matters: the host must
/// refuse to boot without the credentials, not start clean and fail later.
/// </summary>
/// <remarks>
/// <b>Without <c>[Required]</c> and <c>ValidateOnStart</c> together, none of
/// this fails.</b> <c>IOptions&lt;T&gt;</c> always resolves — unbound it hands
/// back a default-constructed instance — so a forgotten binding is invisible to
/// <c>ValidateOnBuild</c>, and a bound class with no annotations validates
/// successfully while empty. The BFF would then start, request a token with an
/// empty <c>client_id</c>, and read Catalog's 401s as Catalog's fault. Each
/// member is removed separately below, because a single test that drops all
/// three would pass against a <c>[Required]</c> on only one of them.
/// </remarks>
public class OptionsValidationTests
{
    /// <summary>
    /// A field rather than a collection expression inside the subclass below,
    /// because CA1861 is an error under ADR-019 and the subclass's
    /// <c>Settings</c> is read on every host build.
    /// </summary>
    private static readonly string[] Members = ["ClientId", "ClientSecret", "Scope"];

    [Theory]
    [InlineData("ClientId")]
    [InlineData("ClientSecret")]
    [InlineData("Scope")]
    public void The_host_refuses_to_start_without_each_credential(string member)
    {
        using MissingSettingFactory factory = new(member);

        // The factory builds the host on first use, so the throw arrives here
        // rather than at construction.
        OptionsValidationException thrown =
            Should.Throw<OptionsValidationException>(() => factory.CreateClient());

        thrown.Message.ShouldContain(member);
    }

    [Fact]
    public void The_host_starts_when_all_three_are_supplied()
    {
        // The other direction, so the theory above cannot pass by the host
        // being unstartable for some unrelated reason.
        using BffFactory factory = new();
        using HttpClient client = factory.CreateClient();

        client.ShouldNotBeNull();
    }

    /// <summary>
    /// The base fixture's settings with one <c>Identity:Client</c> member
    /// removed.
    /// </summary>
    private sealed class MissingSettingFactory(string member) : BffFactory
    {
        protected override IEnumerable<KeyValuePair<string, string?>> Settings =>
        [
            new(AuthenticationExtensions.AuthorityKey, UnreachableAuthority),
            .. Members
                .Where(name => !string.Equals(name, member, StringComparison.Ordinal))
                .Select(name => new KeyValuePair<string, string?>(
                    $"{ServiceIdentityOptions.SectionName}:{name}",
                    "supplied"))
        ];
    }
}
