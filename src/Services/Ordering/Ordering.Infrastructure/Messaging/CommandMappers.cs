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
/// kind of thing a later reader completes by adding the missing endpoint.
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
            throw new ContractMappingException(
                $"Unknown cancellation reason '{message.Reason}' on {nameof(CancelOrder)}.");

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
    /// The closed vocabulary of <see cref="ReviewReasons"/>, read off the class
    /// rather than listed again. A second table is a second thing to forget
    /// when a reason is added, which is the argument
    /// <c>CancellationReasons</c> already makes one file over. The consequence
    /// of letting an unknown code through is sharper here than a bad value in
    /// a column: <c>Reason</c> is half the primary key of
    /// <c>ordering.OrderReviews</c>, so a typo does not overwrite an
    /// escalation — it silently opens a second one nobody resolves, and §13.6
    /// pages on any row older than an hour.
    /// </summary>
    private static readonly FrozenSet<string> Known = FrozenSet.Create(
        StringComparer.Ordinal,
        ReviewReasons.NotDespatched,
        ReviewReasons.StockNotReleased);

    public FlagOrderForReviewCommand Map(FlagOrderForReview message)
    {
        if (!Known.Contains(message.Reason))
            throw new ContractMappingException(
                $"Unknown review reason '{message.Reason}' on {nameof(FlagOrderForReview)}.");

        return new FlagOrderForReviewCommand(message.OrderId, message.Reason);
    }
}
