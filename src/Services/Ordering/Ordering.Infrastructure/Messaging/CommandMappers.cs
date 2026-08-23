using System.Collections.Frozen;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Common.Domain;
using Ordering.Application.Orders;
using Ordering.Application.Orders.CancelOrder;
using Ordering.Application.Orders.ConfirmOrder;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Application.Orders.MarkOrderShipped;
using Ordering.Domain.Orders;

namespace Ordering.Infrastructure.Messaging;

/// <summary>
/// One mapper per command contract (§9.4), and the whole of the wire-to-command
/// boundary for the four commands §3.2 says Ordering accepts.
/// </summary>
/// <remarks>
/// Each parses the wire vocabulary, which is the thing <c>CommandConsumer</c>
/// deliberately does not. A parse that fails throws
/// <see cref="ContractMappingException"/>, which the <c>ordering-commands</c>
/// endpoint excludes from retry (§9.8) — a malformed contract does not become
/// well-formed on the fourth attempt.
/// <para>
/// <b>Declaring the origin is <see cref="CancelOrderMapper"/>'s alone</b>, and
/// the asymmetry is the design rather than an oversight: <c>CancelOrder</c> is
/// the only one of the four with a second way in, so it is the only one §11.4's
/// ownership check has two callers to tell apart. The other three commands
/// carry no <c>CommandOrigin</c> at all — a discriminator with one value is the
/// kind of thing a later reader completes by adding the second ingress it
/// implies. <b>Not "the missing endpoint"</b>: all four are mapped on
/// <c>ordering-commands</c> right here, and what none of the three has is an
/// HTTP route.
/// </para>
/// <para>
/// <b>They are grouped in one file because they are one boundary</b>, the way
/// <c>Common.Contracts.Ordering.V1.Commands</c> groups the four contracts they
/// read. Splitting them would put four ten-line classes in four files whose
/// only shared property is the thing this file's summary states once.
/// </para>
/// </remarks>
public sealed class CancelOrderMapper : ICommandMessageMapper<CancelOrder, CancelOrderCommand>
{
    public CancelOrderCommand Map(CancelOrder message)
    {
        // The same parse the endpoint uses (§11.4), failing differently: a
        // sibling service sending a code we do not know is a deployment
        // problem, and no amount of backoff resolves it.
        if (!CancellationReasons.TryParse(message.Reason, out CancellationReason reason))
        {
            throw new ContractMappingException(
                $"Unknown cancellation reason '{message.Reason}' on {nameof(CancelOrder)}.");
        }

        // CommandOrigin.System, written here and nowhere else. The message
        // carries no origin field, so nothing a peer sends can forge one —
        // arriving on this service's command queue is what earns it (§11.4),
        // with the weakness §9.4's callout states in full.
        return new CancelOrderCommand(message.OrderId, reason, CommandOrigin.System);
    }
}

/// <inheritdoc cref="CancelOrderMapper"/>
public sealed class ConfirmOrderMapper : ICommandMessageMapper<ConfirmOrder, ConfirmOrderCommand>
{
    public ConfirmOrderCommand Map(ConfirmOrder message)
    {
        // A blank or over-long reference is a malformed contract rather than a
        // domain rejection, which is why the DomainException is translated
        // here instead of being left for the handler: the handler's refusals
        // are acked and counted (§9.8), and a payload this service cannot
        // read should reach the error queue on the first attempt.
        try
        {
            return new ConfirmOrderCommand(message.OrderId, PaymentReference.Of(message.PaymentReference));
        }
        catch (DomainException e)
        {
            throw new ContractMappingException(
                $"Unusable payment reference on {nameof(ConfirmOrder)}.", e);
        }
    }
}

/// <inheritdoc cref="CancelOrderMapper"/>
public sealed class MarkOrderShippedMapper : ICommandMessageMapper<MarkOrderShipped, MarkOrderShippedCommand>
{
    public MarkOrderShippedCommand Map(MarkOrderShipped message)
    {
        try
        {
            return new MarkOrderShippedCommand(message.OrderId, TrackingNumber.Of(message.TrackingNumber));
        }
        catch (DomainException e)
        {
            throw new ContractMappingException(
                $"Unusable tracking number on {nameof(MarkOrderShipped)}.", e);
        }
    }
}

/// <inheritdoc cref="CancelOrderMapper"/>
public sealed class FlagOrderForReviewMapper
    : ICommandMessageMapper<FlagOrderForReview, FlagOrderForReviewCommand>
{
    /// <summary>
    /// The closed vocabulary of <see cref="ReviewReasons"/>. The consequence
    /// of letting an unknown code through is sharper here than a bad value in
    /// a column: <c>Reason</c> is half the primary key of
    /// <c>ordering.OrderReviews</c>, so a typo does not overwrite an
    /// escalation — it silently opens a second one nobody resolves, and §13.6
    /// pages on any row older than an hour.
    /// </summary>
    /// <remarks>
    /// <b>This is a second copy of that class and it used to claim it was
    /// not</b> — "read off the class rather than listed again" was the comment
    /// here, over three names typed out by hand. The list stays, because a
    /// validator whose vocabulary is derived by reflection accepts whatever
    /// the class grows next and can never be observed refusing anything; what
    /// closes the drift is a test whose subject is the agreement between the
    /// two, which fails from either side. <c>CancellationReasons</c> one file
    /// over parses rather than lists and is the shape this cannot take, since
    /// a review reason maps to no domain type.
    /// <para>
    /// <b>Public because that test is its only other reader, and until this
    /// change it could not be written.</b> A private set left the suite able
    /// to assert one direction — every declared reason is accepted — which
    /// passes unchanged while a stale code sits here that
    /// <see cref="ReviewReasons"/> no longer declares. One access modifier is
    /// a smaller commitment than an <c>InternalsVisibleTo</c> naming the
    /// consumer, which is the trade <c>MetricsInitialiser</c> already makes.
    /// </para>
    /// </remarks>
    // **Most of these arrive in the same release that starts emitting them,
    // and #131 is the ordering nobody has stated.** An old ordering-commands
    // consumer handed the new FlagOrderForReview refuses the reason below,
    // and ContractMappingException is on this endpoint's retry-ignore list on
    // purpose — so it reaches the error queue on the FIRST attempt rather
    // than after a minute of pointless backoff. Right for a contract that
    // really is malformed; wrong for a well-formed one from a newer producer,
    // which is the case that has no rule. not_confirmed is #126's addition
    // and pays the same toll — the count is left out of this comment because
    // it was "three of these four" until this line added a fifth, which is
    // the drift the vocabulary's own docs warn about one file over.
    public static readonly FrozenSet<string> Known = FrozenSet.Create(
        StringComparer.Ordinal,
        ReviewReasons.NotDespatched,
        ReviewReasons.StockNotReleased,
        ReviewReasons.PaymentAuthorisedDuringCompensation,
        ReviewReasons.CancelledAfterConfirmation,
        ReviewReasons.NotConfirmed);

    public FlagOrderForReviewCommand Map(FlagOrderForReview message)
    {
        if (!Known.Contains(message.Reason))
        {
            throw new ContractMappingException(
                $"Unknown review reason '{message.Reason}' on {nameof(FlagOrderForReview)}.");
        }

        return new FlagOrderForReviewCommand(message.OrderId, message.Reason);
    }
}
