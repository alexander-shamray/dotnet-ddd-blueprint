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

    [Fact]
    public void A_schema_longer_than_sysname_is_refused()
    {
        // 128 characters is what SQL Server's `sysname` holds. Past it the
        // type would construct, and every statement composed from it would
        // fail at runtime instead — a registration-time refusal is the whole
        // point of validating here. The scaffold already stops a service name
        // this long, and a value it lets through must not fail deeper in.
        Should.NotThrow(() => new OutboxTable(new string('c', 128)));
        Should.Throw<ArgumentException>(() => new OutboxTable(new string('c', 129)));
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
