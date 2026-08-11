namespace Common.Application;

/// <summary>
/// Which after-commit destination a staged row is for. One table serves both
/// (§9.4), so both get the same durability, the same retry accounting and the
/// same monitoring — which is the argument against a second, separate
/// mechanism for local reactions.
/// </summary>
public enum OutboxLane
{
    /// <summary>Published to the message broker. A public contract.</summary>
    Broker,

    /// <summary>
    /// Dispatched in-process after commit to
    /// <see cref="IProjectionHandler{TEvent}"/>. Never leaves the service and
    /// is not a contract.
    /// </summary>
    Local
}
