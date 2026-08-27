using Shouldly;
using Xunit;

namespace Common.Web.Tests;

/// <summary>
/// The never-log vocabulary itself (§13.4), asserted as a list rather than
/// through the processor that reads it.
/// </summary>
/// <remarks>
/// <b>The list is the control, so removing a term has to be a deliberate
/// edit.</b> Every other test in this area drives a record through
/// <see cref="SensitiveDataRedactor"/> and asserts one key at a time, which
/// means a term deleted in a refactor takes its own test with it and the suite
/// stays green while the export widens. Pinning the whole array is the only
/// assertion that fails on a deletion.
/// </remarks>
public class SensitiveKeysTests
{
    // The list this repository has decided on, spelled out rather than
    // computed from SensitiveKeys.All — a test that reads the value it is
    // checking cannot notice the value changing, which is the one thing it is
    // here to notice.
    private static readonly string[] Expected =
    [
        "password",
        "passwd",
        "pwd",
        "secret",
        "token",
        "authorization",
        "credential",
        "cookie",
        "apikey",
        "api_key",
        "connectionstring",
        "connection_string",
        "privatekey",
        "private_key",
        "cardnumber",
        "card_number",
        "ssn",
        "nationalid",
        "cvv",
        "otp",
        "sessionid",
        "session_id",
        "accountkey",
        "account_key",
        "signature"
    ];

    [Fact]
    public void The_vocabulary_is_the_one_that_was_decided()
    {
        SensitiveKeys.All.ShouldBe(Expected);
    }

    [Fact]
    public void The_vocabulary_cannot_be_rewritten_through_the_view()
    {
        // `IReadOnlyList<T>` is a static view, not a guarantee: a property
        // returning the backing `string[]` can be cast straight back and
        // mutated, which would rewrite the platform's never-log list at run
        // time and make the pinning test above assert about something the
        // process is no longer using.
        //
        // Asserted as the cast failing rather than as the type being some
        // particular wrapper — the property's contract is that no caller can
        // reach the array, and naming ReadOnlyCollection here would fail the
        // day it becomes an ImmutableArray for a reason that is not a defect.
        (SensitiveKeys.All as string[]).ShouldBeNull(
            "a caller that can cast this back can silently widen what the platform exports");
    }

    [Theory]
    [InlineData("ConnectionString")]
    [InlineData("Catalog__ConnectionString")]
    [InlineData("ApiKey")]
    [InlineData("api_key")]
    [InlineData("Pwd")]
    [InlineData("Passwd")]
    [InlineData("Set-Cookie")]
    [InlineData("PrivateKey")]
    [InlineData("Cvv")]
    [InlineData("SessionId")]
    [InlineData("AccountKey")]
    [InlineData("Signature")]
    public void A_key_this_codebase_actually_uses_is_matched(string key)
    {
        // Named cases rather than a loop over Expected, which would only
        // restate the list above. Each of these is a spelling that appears in
        // this repository's own vocabulary and that the eight-term list this
        // one replaced did NOT match — ConnectionString most of all, since no
        // term in that list was a substring of it and matching is by key.
        SensitiveKeys.Matches(key).ShouldBeTrue(key);
    }

    [Theory]
    [InlineData("ShippingAddress")]
    [InlineData("RequestType")]
    [InlineData("CorrelationId")]
    [InlineData("OrderId")]
    [InlineData("Customer")]
    [InlineData("Currency")]
    public void An_innocent_key_is_not_matched(string key)
    {
        // The half a longer list threatens. Matching is by substring, so a
        // term that happens to sit inside an ordinary word redacts that word
        // everywhere — "Shipping" contains "pin", which is why "pin" is
        // deliberately absent from the list above and why this case names it.
        //
        // ShippingAddress staying here is a claim about §13.4's vocabulary and
        // NOT a claim that an address is uninteresting. This list is selective
        // rather than categorical: it is mostly credentials — the things that
        // must never be logged because logging them grants access — and it
        // deliberately reaches a few personal-data shapes too, card_number, ssn
        // and nationalid among them, because those are catastrophic in a log
        // whoever holds them. Drawing a clean line between credentials and
        // personal data would be tidier and would misdescribe the list.
        //
        // What keeps an address off it is the substring cost above, not a
        // category: the terms are matched inside keys, so a term earns its
        // place by what it would redact everywhere else. §11.7 answers the
        // address instead, and answers it earlier — by keeping the value off
        // the wire rather than out of the log. That is why this key survives
        // the removal of ShippingAddressV1 from OrderConfirmed (ADR-035): no
        // integration event carries an address for a log to leak, and the one
        // place the spelling still appears is PlaceOrder's request body,
        // inbound to the service that owns the data.
        SensitiveKeys.Matches(key).ShouldBeFalse(key);
    }

    [Theory]
    [InlineData("Server=sql,1433;Database=Catalog;User Id=sa;Password=hunter2;Encrypt=False")]
    [InlineData("Server=sql,1433;Database=Catalog;User Id=sa;Pwd=hunter2")]
    [InlineData("server=sql;user id=sa;PASSWORD=hunter2")]
    // ADO.NET tolerates whitespace around the separator, so these four are as
    // valid as the three above and a check for the literal "password=" misses
    // every one of them — a value walking past a guarantee written as
    // "whatever its key is called".
    [InlineData("Server=sql;Database=Catalog;User Id=sa;Password = hunter2")]
    [InlineData("Server=sql;Password	=hunter2")]
    [InlineData("Server=sql;Pwd = hunter2")]
    [InlineData("Server=sql;Password  =  hunter2")]
    public void A_connection_string_is_matched_by_its_value(string value)
    {
        // The half that survives a key nobody predicted: a diagnostic written
        // as logger.LogError(ex, "Cannot reach {Dsn}", cs) names no sensitive
        // key at all.
        SensitiveKeys.LooksLikeSecret(value).ShouldBeTrue();
    }

    [Fact]
    public void A_jwt_is_matched_by_its_value()
    {
        SensitiveKeys.LooksLikeSecret("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.c2ln").ShouldBeTrue();
    }

    [Theory]
    [InlineData("the password was rejected")]      // names it, assigns nothing
    [InlineData("password")]
    [InlineData("Walnut desk")]
    [InlineData("018f4c2e-0000-7000-8000-000000000000")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9")]                       // a prefix, but no dots
    [InlineData("eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0")]       // one dot, not two
    [InlineData("")]
    [InlineData(null)]
    public void An_ordinary_value_is_not_matched(string? value)
    {
        // The positive control's other half. A value check that matched
        // everything would pass every test above while emptying every log
        // record on the platform — the failure §13.1 spends a chapter on.
        SensitiveKeys.LooksLikeSecret(value).ShouldBeFalse();
    }

    [Fact]
    public void A_non_string_value_is_not_matched()
    {
        // Guards the cast rather than the policy: OnEnd hands this whatever
        // was bound to the attribute, and an int has no shape to recognise.
        SensitiveKeys.LooksLikeSecret(42).ShouldBeFalse();
    }
}
