using Web.Bff.Identity;

namespace Web.Bff.Tests;

/// <summary>
/// An <see cref="ITokenCache"/> that answers without a network call and hands
/// out a different token each time.
/// </summary>
/// <remarks>
/// <b>The distinct tokens are the point, not the absence of a provider.</b>
/// §9.7 and §11.5 both claim that the credential handler sits inside the
/// resilience pipeline, so a retried attempt re-attaches a <i>fresh</i> token
/// rather than reusing the dead one that failed. That claim is only observable
/// if two attempts can be told apart, and a constant token makes the correct
/// pipeline and the reversed one produce byte-identical requests — the shape
/// this repository keeps finding: a test that cannot fail in one direction.
/// </remarks>
public sealed class RecordingTokenCache : ITokenCache
{
    private int _issued;

    /// <summary>Every scope this has been asked for, in order.</summary>
    public List<string> Scopes { get; } = [];

    /// <summary>How many tokens have been handed out.</summary>
    public int Issued => _issued;

    public Task<string> GetAsync(string scope, CancellationToken ct)
    {
        lock (Scopes)
        {
            Scopes.Add(scope);
        }

        return Task.FromResult($"token-{Interlocked.Increment(ref _issued)}");
    }
}
