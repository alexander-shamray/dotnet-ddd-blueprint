using Common.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Common.Infrastructure.Inbox;

/// <summary>
/// §9.5's duplicate suppression, configured on a receive endpoint ahead of its
/// consumers with <c>UseConsumeFilter(typeof(InboxFilter&lt;&gt;), context)</c>.
/// If the id is already recorded for this endpoint the message is dropped;
/// otherwise the consumer runs and the id is recorded <em>afterwards</em>.
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
/// <para>
/// <b>The key this suppresses on is chosen by whoever published the message,
/// so this filter is only as trustworthy as the set of principals that may
/// publish to the endpoint (#64).</b> §9.1 makes the envelope
/// <c>MessageId</c> and the transport header one GUID, so a publisher controls
/// both, and a junk message carrying the id a real one will use pre-claims the
/// slot. That is why the drop is counted and logged rather than silent — and
/// why #44's per-service broker credential is a prerequisite for reading this
/// mechanism as a guarantee rather than an improvement.
/// </para>
/// </remarks>
public sealed class InboxFilter<T>(
    DbContext db,
    TimeProvider clock,
    MessagingMetrics metrics,
    ILogger<InboxFilter<T>> log)
    : IFilter<ConsumeContext<T>>
    where T : class
{
    // The three type arguments bind to the template BY POSITION, not by name,
    // so the order here is the order the placeholders appear in.
    private static readonly Action<ILogger, string, Guid, string, Exception?> Suppressed =
        LoggerMessage.Define<string, Guid, string>(
            LogLevel.Debug,
            new EventId(1, nameof(Suppressed)),
            "Inbox dropped {MessageType} {MessageId} on {Endpoint}: already recorded as handled.");

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
        {
            // Drop the duplicate — but say so. A bare `return;` here made the
            // one path on which this platform loses a message on purpose the
            // only path with no signal at all (#64): an inbox hit suppressing
            // a message the service has never seen read exactly like a genuine
            // redelivery, from every dashboard in §13.
            //
            // The counter is the measurable half and the log line is the
            // attributable one. The MessageId is on the log rather than on the
            // counter because it is unbounded — see MessagingMetrics.Suppressed.
            metrics.Suppressed(typeof(T).Name, endpoint);
            Suppressed(log, typeof(T).Name, messageId, endpoint, null);
            return;
        }

        // Ordering matters, and it is the one thing in this file that must not
        // be rearranged: the handler runs FIRST, and the inbox row is only
        // written if it succeeded. Recording before would mark a message
        // handled that never was, losing it permanently on the next delivery —
        // because a suppressed redelivery is not retried, it is dropped.
        await next.Send(context);

        // Added AFTER the consumer, not before it, and that is a correctness
        // fix rather than a tidy-up. Staged before `next.Send`, the row is a
        // *tracked* entity on a context the consumer also uses — and a
        // message-borne command reaches §6.3's TransactionBehavior, whose
        // EfUnitOfWork.ExecuteAsync opens every attempt with
        // `db.ChangeTracker.Clear()` so a retry cannot re-commit the previous
        // attempt's mutations (§7.5, PR-09). That clear takes the pending inbox
        // row with it, `SaveChangesAsync` below then persists nothing, and the
        // command is never deduplicated — silently, on every redelivery.
        //
        // Two mechanisms this blueprint already had, in tension, and neither
        // wrong on its own. The cost of resolving it this way is stated in
        // §9.5's table: a handler running inside the command pipeline has
        // already committed its own transaction by the time control returns
        // here, so its inbox row is a second transaction — the "No" row. A
        // handler that writes through this context and does *not* SaveChanges
        // itself still commits with the row below, which is the "Yes" row and
        // the case IntegrationEventConsumer's handlers are in.
        db.Set<InboxMessage>().Add(new InboxMessage(messageId, endpoint, clock.GetUtcNow()));

        // The registered clock, never DateTimeOffset.UtcNow: RetentionPurgeService
        // computes its cutoff from TimeProvider, and a service that substitutes
        // one — every test host does — would otherwise write rows on the wall
        // clock and purge them against a different one, making new rows look
        // expired or old ones immortal.
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
