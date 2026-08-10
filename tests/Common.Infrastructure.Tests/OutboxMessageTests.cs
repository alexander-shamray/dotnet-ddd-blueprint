using System.Text.Json;
using Common.Application;
using Common.Infrastructure.Outbox;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// <c>Stage</c> is a pure function, so it needs no fixture and gets none —
/// and it is the cheapest guard there is on §9.1's single-identity rule.
/// </summary>
public class OutboxMessageTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 2, 26, 0, TimeSpan.Zero);

    private static readonly MessageTypeMap Types = new([typeof(SampleDomainEvent).Assembly]);

    // No converters: nothing in this assembly needs one, which is the
    // ordinary case. Catalog registers a Money converter because Catalog
    // has a value object; the type takes the list rather than assuming it.
    private static readonly OutboxJson Json = new([]);

    [Fact]
    public void Stage_takes_both_identities_from_the_envelope()
    {
        SampleIntegrationEvent message = new()
        {
            MessageId = Guid.CreateVersion7(),
            CorrelationId = Guid.CreateVersion7(),
            OccurredAt = Now,
            Note = "published"
        };

        OutboxMessage row = OutboxMessage.Stage(
            message,
            OutboxLane.Broker,
            correlationId: Guid.CreateVersion7(),
            now: Now,
            types: Types,
            json: Json);

        // Both from the envelope, not minted here — and CorrelationId in
        // particular, because a caller-supplied one is passed in and ignored
        // for an IIntegrationEvent. That argument being silently used instead
        // is the regression this test exists for: the mapper decides the
        // correlation (§9.3), and a value assigned at staging time would
        // quietly replace that choice.
        row.MessageId.ShouldBe(message.MessageId);
        row.CorrelationId.ShouldBe(message.CorrelationId);
    }

    [Fact]
    public void Stage_mints_an_id_for_a_domain_event_and_takes_the_callers_correlation()
    {
        var correlationId = Guid.CreateVersion7();

        OutboxMessage row = OutboxMessage.Stage(
            new SampleDomainEvent(Now, "raised"),
            OutboxLane.Local,
            correlationId,
            Now,
            Types,
            Json);

        // A Local-lane row carries a domain event, which has no envelope and
        // never reaches a broker — so there is no second identity to disagree
        // with and the row mints its own.
        row.MessageId.ShouldNotBe(Guid.Empty);
        row.CorrelationId.ShouldBe(correlationId);
    }

    [Fact]
    public void Stage_writes_the_persisted_name_and_the_payload()
    {
        OutboxMessage row = OutboxMessage.Stage(
            new SampleDomainEvent(Now, "raised"),
            OutboxLane.Local,
            Guid.CreateVersion7(),
            Now,
            Types,
            Json);

        row.MessageType.ShouldBe(Types.NameOf(typeof(SampleDomainEvent)));
        row.Lane.ShouldBe(OutboxLane.Local);
        row.OccurredAt.ShouldBe(Now);

        // Serialised through the declared type, so a payload staged as
        // `object` still carries the concrete shape — the alternative writes
        // `{}` and the failure is a projection that runs on an empty event.
        JsonSerializer
            .Deserialize<SampleDomainEvent>(row.Payload, Json.Options)
            .ShouldBe(new SampleDomainEvent(Now, "raised"));
    }

    [Fact]
    public void Staging_an_unstageable_type_throws_before_a_row_exists()
    {
        // NameOf is called inside Stage, which is called inside the command's
        // transaction — so this fails the command rather than writing a row
        // the dispatcher would spend ten attempts failing to resolve.
        Should.Throw<InvalidOperationException>(() => OutboxMessage.Stage(
            new NotAMessage("nope"),
            OutboxLane.Broker,
            Guid.CreateVersion7(),
            Now,
            Types,
            Json));
    }

    [Fact]
    public void The_outbox_options_do_not_rescue_a_renamed_member()
    {
        // PropertyNameCaseInsensitive = false is a decision, not a default to
        // inherit: a payload that only round-trips because matching is lenient
        // is a payload that will not survive a rename, and the whole reason
        // §9.4 pins these options is that both sides have to agree.
        string lowered = """{"occurredAt":"2026-08-11T02:26:00+00:00","note":"raised"}""";

        JsonSerializer
            .Deserialize<SampleDomainEvent>(lowered, Json.Options)!
            .Note.ShouldBeNull();
    }
}
