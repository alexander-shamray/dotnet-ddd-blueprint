using MassTransit;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// §9.8's redelivery policy, declared once for the four receive endpoints
/// that apply it.
/// </summary>
/// <remarks>
/// <b>The values were spelled four times before this type existed</b>, once
/// per endpoint, and the comments around them called the shape "§9.8's plain
/// exponential five" — a name for a policy that had no symbol, so the only
/// way to check that four endpoints agreed was to read four call sites and
/// compare sixteen numbers. Naming it makes the agreement structural: an
/// endpoint that wants a different ladder now has to say so.
/// <para>
/// <b>What this type does not decide is what gets retried.</b>
/// <c>ordering-commands</c> ignores <c>ContractMappingException</c> before
/// calling <see cref="Standard"/> — a malformed contract does not parse
/// itself on the fourth attempt — and that exclusion is the endpoint's,
/// because it is a claim about which faults are terminal rather than about
/// how long to wait between attempts. Folding it in here would apply one
/// endpoint's exclusion to the three that never raise it.
/// </para>
/// <para>
/// <see cref="RetryLimit"/> counts <em>retries</em> and not deliveries: the
/// endpoint makes one more attempt than this number, and the wait it produces
/// is that many intervals. §9.6's confirmation wait has to clear that sum —
/// a floor it must exceed rather than the term that decides it, which is
/// §9.4's dispatcher backoff — so the saga's own comment reasons about these
/// by name instead of restating them.
/// </para>
/// </remarks>
internal static class RetryPolicy
{
    /// <summary>How many redeliveries follow the first attempt.</summary>
    public const int RetryLimit = 5;

    /// <summary>The first interval, before any doubling.</summary>
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The ceiling the ladder climbs towards. With
    /// <see cref="RetryLimit"/> retries it is never reached, which is the
    /// arithmetic §9.6's confirmation timeout depends on.
    /// </summary>
    public static readonly TimeSpan MaxInterval = TimeSpan.FromMinutes(1);

    /// <summary>What each interval adds on top of the doubling.</summary>
    public static readonly TimeSpan IntervalDelta = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Applies the policy to one endpoint's retry configurator.
    /// </summary>
    public static void Standard(IRetryConfigurator retry) =>
        retry.Exponential(RetryLimit, MinInterval, MaxInterval, IntervalDelta);
}
