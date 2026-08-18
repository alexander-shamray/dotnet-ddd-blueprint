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
    /// <para>
    /// <b>Nothing enforces it at runtime, and that is worth stating where the
    /// value lives.</b> No host registers request-timeout middleware, so this
    /// is the ceiling the outbound budget is <i>checked against</i> rather than
    /// a timeout any request will hit — which is exactly what §9.7 asks for
    /// ("the ordering is the invariant … what to assert in a
    /// configuration-validation test"), and is less than the word "timeout"
    /// suggests on its own.
    /// </para>
    /// <para>
    /// Making the tier real means request-timeout middleware in every host, and
    /// a 504 in §10.5's table to go with it — a platform decision about the
    /// error contract rather than something the BFF may take alone, which is
    /// why PR-19 named the gap instead of closing it.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(30);
}
