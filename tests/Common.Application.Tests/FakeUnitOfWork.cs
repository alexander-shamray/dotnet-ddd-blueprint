namespace Common.Application.Tests;

/// <summary>
/// A recording <see cref="IUnitOfWork"/>. Every member the behaviour touches
/// writes to the shared <see cref="PipelineLog"/>, so ordering across this
/// fake and <see cref="FakeDomainEventDispatcher"/> is one assertion — the
/// behaviour's whole contract is a sequence, and two separate logs would let
/// the sequence lie.
/// </summary>
public sealed class FakeUnitOfWork(PipelineLog log) : IUnitOfWork
{
    /// <summary>What <see cref="ModifiedAggregateCount"/> reports.</summary>
    public int AggregateCount { get; set; }

    public bool HasActiveTransaction { get; set; }

    public int ModifiedAggregateCount
    {
        get
        {
            log.Add("count");
            return AggregateCount;
        }
    }

    public async Task<TResult> ExecuteAsync<TResult>(
        Func<CancellationToken, Task<TResult>> operation,
        CancellationToken ct)
    {
        log.Add("execute");
        return await operation(ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct)
    {
        log.Add("save");
        return Task.FromResult(0);
    }

    public Task ExecuteRawAsync(string sql, object parameters, CancellationToken ct)
    {
        log.Add("raw");
        return Task.CompletedTask;
    }
}
