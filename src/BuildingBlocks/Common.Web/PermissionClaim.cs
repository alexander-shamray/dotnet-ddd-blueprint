namespace Common.Web;

/// <summary>
/// The claim type a permission travels in (§11.4). Four things have to agree
/// on it and only three of them are code: the endpoint policies, through
/// <see cref="AuthorizationPolicyExtensions.RequirePermission"/>;
/// <see cref="HttpContextCurrentUser.HasPermission"/>, which is what a
/// resource-level check reads; and the test authentication scheme, so a test
/// that grants itself a permission exercises the policy rather than bypassing
/// it.
/// </summary>
/// <remarks>
/// The fourth is the realm's protocol mapper (§11.5), which is configuration
/// rather than code and cannot reference this constant. It is asserted against
/// it instead — <c>RealmImportTests</c> reads the shipped realm and requires
/// the mapper to write this claim, because a realm that writes some other name
/// leaves every policy in the platform unsatisfiable with nothing in the
/// solution compiling differently.
/// </remarks>
public static class PermissionClaim
{
    public const string Type = "permission";
}
