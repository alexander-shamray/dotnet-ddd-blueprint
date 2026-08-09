using Common.Infrastructure.Redis;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// The lock against a real server: NX, expiry, and the token check that
/// keeps a stale handle from releasing the next holder's lock. Each test
/// takes its own lock name, so the shared container never couples them.
/// </summary>
[Collection(nameof(IntegrationCollection))]
public sealed class DistributedLockRedisTests(RedisFixture fixture)
{
    private IDistributedLockFactory Factory() =>
        fixture.BuildProvider("locks").GetRequiredService<IDistributedLockFactory>();

    [Fact]
    public async Task A_held_lock_cannot_be_acquired_again()
    {
        IDistributedLockFactory factory = Factory();

        await using IDistributedLock? first =
            await factory.TryAcquireAsync("contend", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        IDistributedLock? second =
            await factory.TryAcquireAsync("contend", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        first.ShouldNotBeNull();
        second.ShouldBeNull();
    }

    [Fact]
    public async Task Release_frees_the_lock_for_the_next_holder()
    {
        IDistributedLockFactory factory = Factory();

        IDistributedLock? first =
            await factory.TryAcquireAsync("handover", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        await first!.DisposeAsync();

        await using IDistributedLock? second =
            await factory.TryAcquireAsync("handover", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task Expiry_frees_the_lock_without_a_release()
    {
        IDistributedLockFactory factory = Factory();

        IDistributedLock? first =
            await factory.TryAcquireAsync("expire", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        first.ShouldNotBeNull();

        IDistributedLock? second = await WaitForAcquireAsync(factory, "expire");
        second.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_stale_handle_must_not_release_the_next_holders_lock()
    {
        IDistributedLockFactory factory = Factory();

        IDistributedLock? stale =
            await factory.TryAcquireAsync("stolen", TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
        stale.ShouldNotBeNull();

        // The key expires; another holder takes the lock with a new token.
        IDistributedLock? current = await WaitForAcquireAsync(factory, "stolen");
        current.ShouldNotBeNull();

        // The stale token no longer matches: this must delete nothing.
        await stale.DisposeAsync();

        IDistributedLock? intruder =
            await factory.TryAcquireAsync("stolen", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        intruder.ShouldBeNull();
    }

    /// <summary>
    /// Polls acquisition past a short TTL. Bounded at ~5 s of attempts, far
    /// past the 200 ms TTLs above, so a pass is never a lucky race.
    /// </summary>
    private static async Task<IDistributedLock?> WaitForAcquireAsync(IDistributedLockFactory factory, string name)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            IDistributedLock? held =
                await factory.TryAcquireAsync(name, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            if (held is not null)
                return held;

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        return null;
    }
}
