using Catalog.TestSupport;
using Common.Application;
using Catalog.Application.Products.PublishProduct;
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
            new PublishProductCommand("Walnut desk", "https://cdn.example/desk.jpg", 19.99m, "eur"),
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
    public async Task An_invalid_command_is_rejected_before_the_handler_and_leaves_no_row()
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IDispatcher dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();

        await Should.ThrowAsync<FluentValidation.ValidationException>(() =>
            dispatcher.SendAsync(
                new PublishProductCommand("", null, -1m, "x"),
                TestContext.Current.CancellationToken));

        int rows = await fixture.ScalarAsync<int>("SELECT Value = COUNT(*) FROM catalog.Products");
        rows.ShouldBe(0, "ValidationBehavior runs before Transaction opens anything (§6.3)");
    }
}
