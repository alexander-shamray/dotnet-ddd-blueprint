using Common.Application;

namespace Ordering.Application.Orders.FlagOrderForReview;

/// <summary>
/// §9.6's one command that changes no business state. It writes an operations
/// row and stops, and no aggregate is loaded — but <b>not</b> because nothing
/// about the order changed. What the four reasons share is narrower than
/// that: a human now has work the platform has no contract to do, and that is
/// a fact about operations rather than about the order.
///
/// Two of them are a wait that ran out, where the order's own state genuinely
/// has not moved. The other two — <c>cancelled_after_payment</c> and
/// <c>cancelled_after_confirmation</c> — exist because <b>money is
/// authorised and the order is not going to be delivered</b>, and §3.2 gives
/// Ordering no refund command to answer that with. Reading "the process
/// stalled" onto either row describes the opposite of what happened.
///
/// <b>It does not follow that a human owns the money on both.</b> §3.2 has
/// Payments consume <c>OrderCancelled</c> and void an authorisation already
/// taken, so <c>cancelled_after_confirmation</c> — raised by that very
/// publication — has a refund already on its way, and what it needs a person
/// for is Shipping. <c>cancelled_after_payment</c> is the authorisation that
/// arrived after that event, which nothing automatic will reach.
/// <para>
/// <b>This said they exist BECAUSE the order was cancelled, and that is not
/// reliably true of <c>cancelled_after_payment</c>.</b> Reached from a
/// decline or a payment timeout, the saga is in <c>Compensating</c> and
/// <c>CancelOrder</c> is still owed at the state's exit — so the row can be
/// written before the cancellation it is named after. The money is the
/// invariant; the order's state is not.
/// </para> They are two codes rather than one
/// because the operator's first step differs, and
/// <c>ordering.OrderReviews</c> persists no saga state to tell them apart.
/// </summary>
/// <remarks>
/// <b>Written through <see cref="IUnitOfWork"/>, not a second connection.</b>
/// Every command runs inside <c>TransactionBehavior</c> (§6.3); a handler that
/// opens its own connection commits outside that transaction, so its write
/// survives a command that failed. Harmless for an idempotent escalation row,
/// and a data-corruption bug the first time the pattern is copied to a handler
/// that is not. <c>IDbConnectionFactory</c> belongs to queries (§6.5) and to
/// projections, which run after commit by design (ADR-018).
/// </remarks>
public sealed class FlagOrderForReviewHandler(IUnitOfWork unitOfWork, TimeProvider clock)
    : ICommandHandler<FlagOrderForReviewCommand, Result>
{
    public async Task<Result> HandleAsync(FlagOrderForReviewCommand command, CancellationToken ct)
    {
        // Conditional INSERT rather than §9.6's printed IF NOT EXISTS, and the
        // chapter was amended in the same change. Both spellings read and then
        // write, so both race — two deliveries of the same escalation can each
        // find no row and each insert one, and the second violates the primary
        // key rather than being absorbed. The lock hints are what make the
        // read a range lock, so the second caller waits for the first to
        // commit and then sees the row. That is PR-20's finding on §6.6's
        // MERGE, one table over: a guard against duplicates that is not
        // range-locked has the defect it was written to fix.
        //
        // Absorbed rather than upserted, deliberately: RaisedAt is when the
        // work first landed on a human, and a second delivery must not move it
        // forward — §13.6 alerts on how long a review has been outstanding.
        await unitOfWork.ExecuteRawAsync(
            """
            INSERT INTO ordering.OrderReviews (OrderId, Reason, RaisedAt)
            SELECT @OrderId, @Reason, @RaisedAt
            WHERE NOT EXISTS (
                SELECT 1
                FROM ordering.OrderReviews WITH (UPDLOCK, HOLDLOCK)
                WHERE OrderId = @OrderId
                    AND Reason = @Reason);
            """,
            new { command.OrderId, command.Reason, RaisedAt = clock.GetUtcNow() },
            ct);

        // The registered clock rather than SYSDATETIMEOFFSET(), which is what
        // §9.6 printed. RetentionPurgeService already computes its cutoff from
        // TimeProvider for the reason that applies here too: a test host
        // substitutes the clock, and a row written on the server's wall clock
        // is a row no substituted clock can reason about.
        return Result.Success();
    }
}
