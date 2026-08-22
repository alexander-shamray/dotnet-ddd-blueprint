using System.Reflection;
using Common.Application;
using Common.Contracts.Ordering.V1;
using Ordering.Application.Orders.FlagOrderForReview;
using Ordering.Infrastructure.Messaging;
using Shouldly;
using Xunit;

namespace Ordering.Application.Tests;

/// <summary>
/// §9.4's wire-to-command boundary, on the one mapper that validates against a
/// list it keeps itself.
/// </summary>
/// <remarks>
/// <b>The subject is the agreement between two copies of one vocabulary</b>,
/// which is the only thing that can fail from either side. <c>ReviewReasons</c>
/// declares the codes; <c>FlagOrderForReviewMapper</c> holds a
/// <c>FrozenSet</c> of the ones it accepts, and a code in the first and not the
/// second is a message this service refuses on the first attempt (§9.8) — for
/// an escalation §13.6 pages on the absence of. The set is not derived by
/// reflection on purpose: a validator whose vocabulary is whatever the class
/// grows next can never be observed refusing anything.
/// <para>
/// It lives beside §12.5's saga suite because it is the same shape — a claim
/// about <c>Ordering.Infrastructure</c> that needs no infrastructure to make.
/// </para>
/// </remarks>
public class CommandMapperTests
{
    private static readonly Guid Order = Guid.Parse("8b3a5c21-4d7e-4f19-8c62-3e5a7b9d1c04");

    public static TheoryData<string> ReviewReasonCodes()
    {
        // Read off the class rather than listed here, which is what makes this
        // a check on the mapper rather than a third copy of the vocabulary.
        string[] codes =
        [
            .. typeof(ReviewReasons)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(f => f is { IsLiteral: true, IsInitOnly: false })
                .Select(f => (string)f.GetRawConstantValue()!)
        ];

        // The gate-coverage rule: a reflection query that silently matches
        // nothing passes every assertion under it. Three today, and the number
        // is asserted as a floor rather than as an equality so that adding a
        // fourth reason does not fail a test that has no opinion about it.
        codes.Length.ShouldBeGreaterThanOrEqualTo(3);

        TheoryData<string> data = [];
        foreach (string code in codes)
            data.Add(code);

        return data;
    }

    [Theory]
    [MemberData(nameof(ReviewReasonCodes))]
    public void Every_declared_review_reason_is_accepted(string code)
    {
        FlagOrderForReviewMapper mapper = new();

        FlagOrderForReviewCommand mapped = mapper.Map(new FlagOrderForReview(Order, code));

        mapped.OrderId.ShouldBe(Order);
        mapped.Reason.ShouldBe(code);
    }

    [Fact]
    public void A_code_the_vocabulary_does_not_declare_is_refused()
    {
        // The other side of the same claim, and the reason the set is a list
        // rather than a reflection query: this is the case a derived
        // vocabulary could never produce. Reason is half the primary key of
        // ordering.OrderReviews, so an unknown code does not overwrite an
        // escalation — it opens a second one nobody resolves.
        FlagOrderForReviewMapper mapper = new();

        Should.Throw<ContractMappingException>(
            () => mapper.Map(new FlagOrderForReview(Order, "cancelled_after_payent")));
    }
}
