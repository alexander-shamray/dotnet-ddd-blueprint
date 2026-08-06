using Shouldly;
using Xunit;

namespace Common.Application.Tests;

public class ErrorTests
{
    [Fact]
    public void A_not_found_error_asks_for_a_404()
    {
        Error error = Error.NotFound("order.not_found", "No order with that id.");

        error.Type.ShouldBe(ErrorType.NotFound);
    }

    [Fact]
    public void A_rule_error_asks_for_a_422()
    {
        Error error = Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");

        error.Type.ShouldBe(ErrorType.Rule);
    }

    [Fact]
    public void An_unavailable_error_asks_for_a_503()
    {
        Error error = Error.Unavailable("pricing.unreachable", "The price list could not be read.");

        error.Type.ShouldBe(ErrorType.Unavailable);
    }

    [Fact]
    public void An_error_keeps_the_code_and_description_it_was_given()
    {
        Error error = Error.NotFound("order.not_found", "No order with that id.");

        error.Code.ShouldBe("order.not_found");
        error.Description.ShouldBe("No order with that id.");
    }

    [Fact]
    public void Two_errors_with_the_same_parts_are_the_same_error()
    {
        // Value equality is what lets a handler test assert against the
        // catalogue — `result.Error.ShouldBe(OrderErrors.NotFound)` — instead of
        // comparing code strings and missing a changed type.
        Error one = Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");
        Error other = Error.Rule("order.already_shipped", "A shipped order cannot be cancelled.");

        one.ShouldBe(other);
    }

    [Fact]
    public void There_are_exactly_three_error_types()
    {
        // §10.5: three cases, not four. There is deliberately no Validation
        // member — a malformed request is rejected by ValidationBehavior before
        // any handler runs, so no handler can return one, and a fourth member
        // here would put two producers on one status code.
        Enum.GetValues<ErrorType>().ShouldBe([ErrorType.NotFound, ErrorType.Rule, ErrorType.Unavailable]);
    }
}
