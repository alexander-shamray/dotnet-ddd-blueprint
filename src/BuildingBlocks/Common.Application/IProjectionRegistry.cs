using Common.Domain;

namespace Common.Application;

/// <summary>
/// Answers whether an event type has any registered projection handler, so the
/// dispatcher does not stage <c>Local</c> rows nobody will consume (§7.5).
/// </summary>
/// <remarks>
/// This is one of the five sites §9.4 tabulates where <c>GetServices&lt;T&gt;</c>
/// returning nothing has to mean something explicit. Here empty means "this
/// event has no projection" — the question being asked — so it returns false
/// and no row is staged. In <c>ProjectionInvoker</c> the same emptiness means
/// a row was staged, so a handler <em>was</em> found earlier, and it throws.
/// The two checks read the same source, which is what makes a handler that is
/// implemented but never registered fail at the first assertion rather than
/// becoming an invisible no-op.
/// </remarks>
public interface IProjectionRegistry
{
    bool HasHandler(IDomainEvent domainEvent);
}
