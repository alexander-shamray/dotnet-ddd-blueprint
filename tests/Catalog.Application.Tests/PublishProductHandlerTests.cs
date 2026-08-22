using System.Text.Json;
using Catalog.Application.Products.PublishProduct;
using Catalog.Domain.Products;
using Catalog.TestSupport;
using Catalog.TestSupport.Outbox;
using Common.Application;
using Common.Contracts.Catalog.V1;
using Common.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Catalog.Application.Tests;

/// <summary>
/// §12.1's application level: one handler end to end, real database, resolved
/// through the real container — dispatcher, pipeline, transaction behaviour,
/// repository, EF — so what is proved is the slice, not a re-wiring of it.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class PublishProductHandlerTests(ServiceFixture fixture) : IAsyncLifetime
{
    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task A_dispatched_command_commits_the_product()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<Guid> result = await dispatcher.SendAsync(
            new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", "https://cdn.example/desk.jpg", 19.99m, "eur"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        // The handler never called SaveChanges — a committed row is the
        // transaction behaviour doing its half (§6.3), which is the point of
        // testing through the dispatcher rather than newing the handler up.
        string name = await fixture.ScalarAsync<string>(
            "SELECT Value = Name FROM catalog.Products WHERE Id = {0}", result.Value);
        name.ShouldBe("Walnut desk");

        string currency = await fixture.ScalarAsync<string>(
            "SELECT Value = PriceCurrency FROM catalog.Products WHERE Id = {0}", result.Value);
        currency.ShouldBe("EUR", "Money.Of normalises the code on the way in");
    }

    [Fact]
    public async Task The_product_row_and_the_outbox_row_commit_together()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        Result<Guid> result = await dispatcher.SendAsync(
            new PublishProductCommand(Guid.CreateVersion7(), "Walnut desk", null, 19.99m, "eur"),
            TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        IReadOnlyList<OutboxMessage> outbox = await fixture.OutboxAsync();

        // Nothing has run yet, and this is only meaningful because the factory
        // removed the hosted dispatcher — otherwise it races a background
        // service that drains these rows twice a second.
        outbox.ShouldAllBe(m => m.ProcessedAt == null);

        // The Broker lane carries the CONTRACT type (§9.3's allow-list)...
        OutboxMessage row = outbox.ShouldHaveSingleItem();
        row.Lane.ShouldBe(OutboxLane.Broker);
        row.MessageType.ShouldBe(typeof(ProductPublished).FullName);

        // ...and the domain type must never reach the broker — the leak §9.3
        // exists to prevent, and it is only checkable because the names differ.
        row.MessageType.ShouldNotContain(nameof(ProductPublishedDomainEvent));

        // No Local row, and that is the IProjectionRegistry contract observed
        // from outside (§7.5): Catalog registers no IProjectionHandler, so
        // staging one would put a row in the table that §9.4 throws on when it
        // finds no handler for it. Catalog's first projection is what changes
        // this line.
        outbox.ShouldNotContain(m => m.Lane == OutboxLane.Local);

        // The correlation is the product, not an ambient request id (§9.3).
        row.CorrelationId.ShouldBe(result.Value);

        // The payload survives the round trip the dispatcher will make of it,
        // Money included — the converter is registered, not merely written.
        ProductPublished published = JsonSerializer
            .Deserialize<ProductPublished>(row.Payload, fixture.OutboxJson.Options)!;
        published.MessageId.ShouldBe(row.MessageId, "one identity, not two (§9.1)");
        published.Amount.ShouldBe(19.99m);
        published.Currency.ShouldBe("EUR");
    }

    [Fact]
    public async Task A_rejected_command_never_reaches_the_outbox()
    {
        // Validation runs before Transaction opens anything (§6.3), so this
        // proves the row is never staged — not that a staged row rolls back.
        // The test below is the one that proves the second thing, and the two
        // are separate on purpose: this assertion would hold even if staging
        // wrote outside the aggregate's transaction, because staging never
        // happens at all.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
            dispatcher.SendAsync(
                new PublishProductCommand(Guid.CreateVersion7(), "", null, -1m, "x"),
                TestContext.Current.CancellationToken));

        (await fixture.OutboxAsync()).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_command_that_fails_after_staging_leaves_neither_row()
    {
        // The other half of atomicity, and the half a rejection cannot show: a
        // row staged inside a transaction that then rolls back would announce
        // a state change that did not happen — the dual write in reverse, and
        // the one the broker cannot take back.
        //
        // StageThenFailCommand publishes two aggregates, so §6.3's
        // one-aggregate assertion throws *after* DispatchAsync has staged an
        // event for each. That is the only failure point on the far side of
        // staging that needs no fault injection and no seam in production
        // code.
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<InvariantViolationException>(() =>
            dispatcher.SendAsync(
                new StageThenFailCommand("Walnut desk"),
                TestContext.Current.CancellationToken));

        // Neither the aggregates nor the rows their events produced.
        (await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products"))
            .ShouldBe(0);
        (await fixture.OutboxAsync()).ShouldBeEmpty(
            "the staged rows must roll back with the transaction that staged them");
    }

    [Fact]
    public async Task A_payload_longer_than_the_string_convention_survives_the_column()
    {
        // §7.2's convention caps every string property at 400 characters, and
        // the generated migration wrote `nvarchar(max)` and `maxLength: 400`
        // on the same line until the model's max length was cleared too. A
        // payload is unbounded by nature — a fat-enough event (§9.1) with a
        // dozen lines on it passes 400 without trying — so this asserts the
        // column rather than the setting.
        //
        // Staged directly rather than through a command: the validator caps
        // Name at 200 and ThumbnailUrl at 400, so no valid command can
        // produce a payload long enough to prove anything here. That is the
        // slice being small, not the column being safe.
        string note = new('a', 1_000);

        await fixture.StageOutboxAsync(OutboxRows.Verbose(fixture, note));

        OutboxMessage row = (await fixture.OutboxAsync()).ShouldHaveSingleItem();
        row.Payload.ShouldContain(note, Case.Sensitive);
    }

    [Fact]
    public async Task An_invalid_command_is_rejected_before_the_handler_and_leaves_no_row()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
            dispatcher.SendAsync(
                new PublishProductCommand(Guid.CreateVersion7(), "", null, -1m, "x"),
                TestContext.Current.CancellationToken));

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "ValidationBehavior runs before Transaction opens anything (§6.3)");
    }
}
