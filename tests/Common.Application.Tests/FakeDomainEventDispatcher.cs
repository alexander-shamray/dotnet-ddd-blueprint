namespace Common.Application.Tests;

/// <summary>Records the dispatch into the shared pipeline log.</summary>
public sealed class FakeDomainEventDispatcher(PipelineLog log) : IDomainEventDispatcher
{
    public Task DispatchAsync(CancellationToken ct)
    {
        log.Add("dispatch");
        return Task.CompletedTask;
    }
}
