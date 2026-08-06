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
        typeof(IQueryHandler<,>)             // §6.5

        // Three more join this list in the PRs that define them:
        // IProjectionHandler<> (§7.5, the local outbox lane), and
        // IIntegrationEventHandler<> and ICommandMessageMapper<,> (§9.4, the
        // broker lane and the wire contract → command mapper). Listing an
        // interface before it exists would not compile; listing one that
        // exists and is never scanned is the trap this class was built for.
        //
        // IPipelineBehavior<,> is deliberately absent, and stays absent.
        // Registration order is pipeline order (§6.3), and a scan gives no
        // ordering guarantee — behaviours are registered explicitly and
        // asserted by a test.
    ];
}
