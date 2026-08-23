using System.Collections.Concurrent;

namespace Common.Application.Tests;

/// <summary>
/// An in-memory <see cref="IIdempotencyStore"/> that records what it was asked
/// to do. §8.5's decisions are almost entirely about <em>which</em> store call
/// happens on which path, so the call log is the assertion surface and the
/// stored state is secondary.
/// </summary>
/// <remarks>
/// <b>Not a Redis substitute, and the difference matters for one test.</b>
/// <see cref="TryClaimAsync"/> is atomic here only because
/// <see cref="ConcurrentDictionary{TKey,TValue}.TryAdd"/> is; nothing about
/// this double proves the real store's <c>SET NX</c> is. That claim belongs to
/// the Redis suite, against a container.
/// </remarks>
internal sealed class RecordingIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyEntry> _entries = new();

    /// <summary>Every call, in order, as <c>verb key</c>.</summary>
    public List<string> Calls { get; } = [];

    /// <summary>Set to throw from <see cref="CompleteAsync"/>, for the hold case.</summary>
    public Exception? CompleteFault { get; set; }

    public IReadOnlyDictionary<string, IdempotencyEntry> Entries => _entries;

    public Task<bool> TryClaimAsync(string key, TimeSpan retention, CancellationToken ct)
    {
        Calls.Add($"claim {key}");
        return Task.FromResult(_entries.TryAdd(key, new IdempotencyEntry(true, null)));
    }

    public Task<IdempotencyEntry?> GetAsync(string key, CancellationToken ct)
    {
        Calls.Add($"get {key}");
        return Task.FromResult(_entries.TryGetValue(key, out IdempotencyEntry? entry) ? entry : null);
    }

    public Task CompleteAsync(string key, string payload, TimeSpan retention, CancellationToken ct)
    {
        Calls.Add($"complete {key}");

        if (CompleteFault is not null)
            return Task.FromException(CompleteFault);

        _entries[key] = new IdempotencyEntry(false, payload);
        return Task.CompletedTask;
    }

    public Task ReleaseAsync(string key, CancellationToken ct)
    {
        Calls.Add($"release {key}");
        _entries.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Plants a completed entry, standing in for an earlier attempt that
    /// committed. The behaviour's replay path is what reads it.
    /// </summary>
    public void Completed(string key, string payload) =>
        _entries[key] = new IdempotencyEntry(false, payload);

    /// <summary>Plants a claim nobody finished — the in-flight case.</summary>
    public void InFlight(string key) =>
        _entries[key] = new IdempotencyEntry(true, null);
}

/// <summary>
/// A caller, as <see cref="ICurrentUser"/> sees one. Two factories rather than
/// a constructor flag, because §8.5's subject segment turns on exactly the
/// distinction their names make.
/// </summary>
internal sealed class StubCurrentUser : ICurrentUser
{
    private readonly Guid? _id;

    private StubCurrentUser(Guid? id)
    {
        _id = id;
    }

    public bool IsAuthenticated => _id is not null;

    public Guid Id => _id ?? throw new InvalidOperationException("No authenticated caller.");

    public static StubCurrentUser Authenticated(Guid id) => new(id);

    /// <summary>
    /// No caller — which §8.5 is careful to say covers an anonymous HTTP
    /// request and a message-borne command alike, not just the second.
    /// </summary>
    public static StubCurrentUser Anonymous() => new(null);

    public bool HasPermission(string permission) => false;
}
