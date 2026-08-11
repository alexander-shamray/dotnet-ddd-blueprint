namespace Common.Application.Tests;

/// <summary>
/// One implementation per entry in <see cref="PluggableInterfaces.All"/> that
/// the request types next door do not already cover, so the §6.2 scan is
/// exercised for every interface rather than for the ones a handler happens to
/// implement.
/// </summary>
/// <remarks>
/// <b>These exist because the registration test could not fail for a missing
/// entry.</b> It derives the implementations it expects by asking
/// <c>PluggableInterfaces.All</c> what to look for — so deleting an entry
/// removed the interface from the production scan <em>and</em> from the test
/// that guards it, and every test stayed green over a handler nothing would
/// ever invoke. That is the exact trap <c>PluggableInterfaces</c>' own comment
/// says the class was built to prevent, reachable through the guard rather than
/// around it.
/// <para>
/// The control that uses them names each closed type in source, so it fails on
/// the deletion rather than following it.
/// </para>
/// </remarks>
public sealed record ScannedEvent(Guid Id);

public sealed class ScannedEventHandler : IIntegrationEventHandler<ScannedEvent>
{
    public Task HandleAsync(ScannedEvent integrationEvent, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>A wire contract, standing in for one a service would accept.</summary>
public sealed record ScannedMessage(Guid Id);

/// <summary>The application command it maps to.</summary>
public sealed record ScannedCommand(Guid Id) : ICommand<Result>;

public sealed class ScannedCommandMapper : ICommandMessageMapper<ScannedMessage, ScannedCommand>
{
    public ScannedCommand Map(ScannedMessage message) => new(message.Id);
}

/// <summary>A projection handler, for the third of the five.</summary>
public sealed class ScannedProjection : IProjectionHandler<ScannedEvent>
{
    public Task HandleAsync(ScannedEvent domainEvent, CancellationToken ct) => Task.CompletedTask;
}
