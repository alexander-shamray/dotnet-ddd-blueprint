using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Common.Infrastructure.Inbox;

/// <summary>
/// §9.5's duplicate suppression, configured on a receive endpoint ahead of its
/// consumers with <c>UseConsumeFilter(typeof(InboxFilter&lt;&gt;), context)</c>.
/// Before handling a message it records the id; if the id is already recorded
/// for this endpoint, the message is dropped.
/// </summary>
/// <remarks>
/// <b>Common, not per-service, and the chapter's sample says otherwise for the
/// reason PR-14 already corrected once.</b> §9.5 writes
/// <c>InboxFilter&lt;T&gt;(OrderingDbContext db)</c> because that chapter is
/// written from Ordering's point of view — the same viewpoint that had §9.4
/// writing <c>ordering.OutboxMessages</c> into code every service shares.
/// Nothing in this filter is per-service, so six copies would be six places for
/// the ordering below to be got wrong.
/// <para>
/// <b>The <c>DbContext</c> must be the service's own, resolved and not
/// constructed.</b> Sharing the handler's transaction is the entire reason this
/// writes through EF rather than through <c>IDbConnectionFactory</c>, so each
/// service registers
/// <c>AddScoped&lt;DbContext&gt;(sp =&gt; sp.GetRequiredService&lt;XDbContext&gt;())</c>.
/// <c>AddScoped&lt;DbContext, XDbContext&gt;()</c> compiles and resolves and is
/// wrong: it builds a <em>second</em> context in the same scope, so the inbox
/// row commits in its own transaction and §9.5's atomic row silently becomes
/// its non-atomic one.
/// </para>
/// <para>
/// And even resolved correctly it is duplicate <em>suppression</em>, only
/// sometimes an atomic guarantee: a handler writing through Dapper on its own
/// connection commits separately, so a crash between <c>next.Send</c> returning
/// and <c>SaveChangesAsync</c> leaves the work done and the message unrecorded.
/// That is acceptable because handlers are idempotent anyway — this removes the
/// common duplicate, not every duplicate (§9.5).
/// </para>
/// </remarks>
public sealed class InboxFilter<T>(DbContext db) : IFilter<ConsumeContext<T>>
    where T : class
{
    public async Task Send(ConsumeContext<T> context, IPipe<ConsumeContext<T>> next)
    {
        // The transport's id, which for an integration event is also the
        // envelope's and the outbox row's — one GUID, and §9.1 says why at
        // length. A message with none cannot be deduped at all, and acking it
        // silently would be the loss this filter exists to prevent.
        Guid messageId = context.MessageId ??
            throw new InvalidOperationException("Message has no MessageId.");

        // The queue this message arrived on — the same type on a different
        // endpoint is a different unit of work.
        string endpoint = context.ReceiveContext.InputAddress.AbsolutePath.TrimStart('/');

        bool alreadyHandled = await db
            .Set<InboxMessage>()
            .AnyAsync(
                m => m.MessageId == messageId && m.Endpoint == endpoint,
                context.CancellationToken);

        if (alreadyHandled)
            return;                     // Silently drop the duplicate.

        db.Set<InboxMessage>().Add(new InboxMessage(messageId, endpoint, DateTimeOffset.UtcNow));

        // Ordering matters, and it is the one thing in this file that must not
        // be rearranged: the handler runs FIRST, and the inbox row is only
        // committed if it succeeded. Recording before would mark a message
        // handled that never was, losing it permanently on the next delivery —
        // because a suppressed redelivery is not retried, it is dropped.
        await next.Send(context);
        await db.SaveChangesAsync(context.CancellationToken);
    }

    /// <summary>
    /// MassTransit's diagnostic probe. Required by <see cref="IFilter{T}"/> and
    /// absent from §9.5's sample, which is an excerpt rather than a compilable
    /// unit (Appendix D) — the scope name is what identifies this filter in
    /// <c>bus.GetProbeResult()</c>.
    /// </summary>
    public void Probe(ProbeContext context) => context.CreateFilterScope("inbox");
}
