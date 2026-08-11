using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The schema is the one value in this codebase interpolated into SQL rather
/// than parameterised, because a schema cannot be a parameter. What cannot be
/// a parameter has to be a value the type refuses to hold wrongly.
/// </summary>
public class OutboxTableTests
{
    [Fact]
    public void The_table_is_schema_qualified_and_delimited()
    {
        new OutboxTable("catalog").QualifiedName.ShouldBe("[catalog].OutboxMessages");
    }

    [Fact]
    public void A_schema_that_is_a_reserved_word_still_parses()
    {
        // The scaffold accepts a service called `User`, and `user` is
        // reserved — `FROM user.OutboxMessages` is not a schema reference SQL
        // Server can read. Brackets rather than a keyword blacklist, which
        // would need extending with every release.
        new OutboxTable("user").QualifiedName.ShouldBe("[user].OutboxMessages");
    }

    [Theory]
    [InlineData("catalog; DROP TABLE catalog.Products --")]
    [InlineData("catalog.OutboxMessages")]
    [InlineData("[catalog]")]
    [InlineData("9catalog")]
    [InlineData("")]
    [InlineData(" catalog")]
    public void Anything_that_is_not_an_identifier_is_refused(string schema)
    {
        Should.Throw<ArgumentException>(() => new OutboxTable(schema));
    }
}
