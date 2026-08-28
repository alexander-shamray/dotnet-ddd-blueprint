namespace Common.Application.Tests;

/// <summary>
/// A recording <see cref="IIdempotencyMarkerStore"/>. It writes into the same
/// <see cref="PipelineLog"/> as <see cref="FakeUnitOfWork"/> and
/// <see cref="FakeDomainEventDispatcher"/>, for the reason that file gives: the
/// behaviour's whole contract is a sequence, and the marker's two calls have to
/// be placed in it rather than merely counted.
/// </summary>
/// <remarks>
/// <b>Where each call lands is the assertion, not that it happened.</b> A read
/// after the handler would answer the wrong question — the work would already
/// have run — and a write before the aggregate-count guard would leave a marker
/// behind for a command §6.3 is about to refuse, which is a permanent refusal
/// of every retry of a command that never committed.
/// </remarks>
public sealed class RecordingMarkerStore(PipelineLog log) : IIdempotencyMarkerStore
{
    /// <summary>Keys a previous attempt is to be reported as having committed.</summary>
    public HashSet<string> Committed { get; } = [];

    /// <summary>Keys this run wrote, in order.</summary>
    public List<string> Written { get; } = [];

    public Task<bool> ExistsAsync(string key, CancellationToken ct)
    {
        log.Add("marker-read");
        return Task.FromResult(Committed.Contains(key));
    }

    public Task MarkAsync(string key, CancellationToken ct)
    {
        log.Add("marker-write");
        Written.Add(key);
        return Task.CompletedTask;
    }
}
