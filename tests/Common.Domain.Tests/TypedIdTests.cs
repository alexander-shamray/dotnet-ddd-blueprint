using Shouldly;
using Xunit;

namespace Common.Domain.Tests;

/// <summary>
/// §5.2's pattern has no base type and no interface — it is a shape, and these
/// tests pin the three properties every identifier written to it must have.
/// `Common.Domain` ships no type for them to run against, which is the point:
/// the cost of the pattern is that each service restates it, and the benefit is
/// that `GetOrder(customerId)` does not compile.
/// </summary>
public class TypedIdTests
{
    [Fact]
    public void A_new_identifier_is_version_7()
    {
        var id = TestId.New();

        // v7 rather than v4 — the factory's one observable commitment.
        // Whether a time-ordered Guid actually helps SQL Server's clustered
        // index is §5.2's separate claim, and an open one; nothing here
        // asserts it.
        id.Value.Version.ShouldBe(7);
    }

    [Fact]
    public void Every_new_identifier_is_distinct()
    {
        TestId[] ids =
        [
            .. Enumerable
                .Range(0, 1_000)
                .Select(_ => TestId.New())
        ];

        ids.Distinct().Count().ShouldBe(ids.Length);
    }

    [Fact]
    public void Identifiers_wrapping_the_same_value_are_equal()
    {
        var value = Guid.CreateVersion7();

        var one = new TestId(value);
        var other = new TestId(value);

        one.ShouldBe(other);
    }

    [Fact]
    public void An_identifier_prints_as_its_underlying_value()
    {
        var value = Guid.CreateVersion7();

        new TestId(value).ToString().ShouldBe(value.ToString());
    }

    [Fact]
    public void Two_identifier_types_over_the_same_value_are_not_interchangeable()
    {
        var value = Guid.CreateVersion7();

        var id = new TestId(value);
        var other = new OtherTestId(value);

        // The compiler already refuses `TestId x = other;` — this asserts the
        // runtime half, so a future conversion operator cannot quietly make the
        // two the same thing.
        id.Equals(other).ShouldBeFalse();
    }
}
