namespace Common.Application;

/// <summary>
/// Every open generic the container is expected to discover by convention.
/// Adding a pluggable interface means adding it here — and nowhere else.
/// </summary>
/// <remarks>
/// The list is declared once and read twice: by <c>AddPluggableFrom</c> and by
/// the registration test that guards it. Two copies drift together or not at
/// all, and the guard silently stops covering whatever the newest interface is
/// (§6.2).
/// </remarks>
public static class PluggableInterfaces
{
    public static readonly IReadOnlyList<Type> All =
    [
        typeof(ICommandHandler<,>),          // §6.2 — HTTP and message-borne
        typeof(IQueryHandler<,>),            // §6.5
        typeof(IProjectionHandler<>),        // §7.5 — the local outbox lane
        typeof(IIntegrationEventHandler<>),  // §9.4 — another service's events
        typeof(ICommandMessageMapper<,>)     // §9.4 — wire contract → command

        // The list is complete at five, and the last two joined with PR-15's
        // consumers rather than earlier: listing an interface before it exists
        // would not compile, and listing one that exists and is never scanned
        // is the trap this class was built for — the scan is the *only*
        // registration a handler gets, so a missing entry is a message that
        // reaches §9.4's throw with the dashboards green.
        //
        // IPipelineBehavior<,> is deliberately absent, and stays absent.
        // Registration order is pipeline order (§6.3), and a scan gives no
        // ordering guarantee — behaviours are registered explicitly and
        // asserted by a test.
    ];
}
