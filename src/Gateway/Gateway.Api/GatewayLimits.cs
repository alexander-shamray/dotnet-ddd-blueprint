namespace Gateway.Api;

/// <summary>
/// The edge's request body ceiling (§10.1), which is a platform decision
/// rather than a framework default.
/// </summary>
public static class GatewayLimits
{
    /// <summary>
    /// One mebibyte, and the only body ceiling in the platform.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every request this platform accepts is a JSON command, and the largest
    /// one it can construct is an order at <c>PlaceOrderValidator.MaxItems</c>
    /// — a hundred lines, so tens of kilobytes. A mebibyte is two orders of
    /// magnitude above that and two below what an upload endpoint would want,
    /// which is the shape of a limit chosen for a platform that has no uploads
    /// rather than one inherited from a web server that might.
    /// </para>
    /// <para>
    /// A constant rather than configuration, on §15.4's test: it does not vary
    /// between environments, so binding it would be a settings bag holding one
    /// value that is the same everywhere. A named constant rather than a
    /// literal in <c>Program.cs</c> because the tests spend it from both sides
    /// of the boundary, and reading it here is what lets them assert the
    /// boundary's <i>semantics</i> — at the ceiling forwarded, one byte past
    /// refused — rather than a value. A suite carrying its own copy of the
    /// number would go red the day the decision changed, reporting a
    /// disagreement between two literals as though it were a defect in the
    /// gateway.
    /// </para>
    /// </remarks>
    public const long MaxRequestBodyBytes = 1L * 1024 * 1024;
}
