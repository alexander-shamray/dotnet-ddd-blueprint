namespace Common.Web;

/// <summary>
/// §15.4's static constants — the tier of settings that are genuinely not
/// configuration, and are therefore not bound, not validated and not
/// deployable.
/// </summary>
/// <remarks>
/// <b>The name is §15.4's and the class is deliberately not an options
/// type.</b> §15.4's test for what deserves binding is whether any member would
/// differ between Compose, the test fixture and production; nothing here does,
/// and a bag bound to a section nobody sets would gate boot on configuration
/// that never varies. <c>ServiceIdentityOptions</c> in <c>Web.Bff</c> is the
/// only options type in the solution, and it earns that by holding a secret.
/// <para>
/// It lives in <c>Common.Web</c> rather than in the one host that reads it
/// today, because the hierarchy this caps is the platform's: §9.7 requires
/// timeouts to decrease inwards at every level, and the middle tier is a claim
/// about what any host here will spend on one request, not about what the BFF
/// spends on its hop.
/// </para>
/// </remarks>
public static class ServiceOptions
{
    /// <summary>
    /// The ceiling §9.7's timeout hierarchy asserts against — a service's total
    /// budget for one operation, which every outbound client total must sit
    /// below.
    /// </summary>
    /// <remarks>
    /// Thirty seconds is the top of §9.7's 10–30 s band, and it is chosen at
    /// the top rather than the middle because the band's job is to be
    /// comfortably under the gateway's 30–60 s and comfortably over the 5 s
    /// outbound total. A compile-time invariant, not a deployable value.
    /// </remarks>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
}
