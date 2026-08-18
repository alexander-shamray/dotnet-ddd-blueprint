using System.ComponentModel.DataAnnotations;

namespace Web.Bff.Identity;

/// <summary>
/// §15.4's only options type in the solution, and this is the only host that
/// binds it. The BFF is the one host that calls a peer synchronously (§9.7),
/// so it is the one host holding client credentials (§11.5) — and a binding
/// hoisted into <c>Common.Web</c> for tidiness would re-impose the requirement
/// on every host, which is precisely what §15.3 spent a section undoing.
/// </summary>
/// <remarks>
/// <b>It earns its options type by holding a secret that differs per
/// environment.</b> §15.4's test for what is not configuration is whether any
/// member would vary between Compose, the fixture and production;
/// <c>ClientSecret</c> does, which is what separates this from
/// <c>ServiceOptions</c> in <c>Common.Web</c>.
/// <para>
/// <c>[Required]</c> on all three is what makes <c>ValidateDataAnnotations</c>
/// do anything — a bound options class with no annotations validates
/// successfully while empty, and an unbound <c>IOptions&lt;T&gt;</c> always
/// resolves to a default-constructed instance. Without these the BFF would
/// start clean and request a token with an empty scope, then read Catalog's
/// 401s as Catalog's fault.
/// </para>
/// </remarks>
public sealed class ServiceIdentityOptions
{
    /// <summary>The configuration section, named once (§15.4).</summary>
    public const string SectionName = "Identity:Client";

    [Required] public string ClientId { get; init; } = "";

    [Required] public string ClientSecret { get; init; } = "";

    [Required] public string Scope { get; init; } = "";
}
