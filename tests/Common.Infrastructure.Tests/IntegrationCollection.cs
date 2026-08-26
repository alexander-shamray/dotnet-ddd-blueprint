using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §12.4's per-assembly declaration, on the third assembly that needs Docker.
/// </summary>
/// <remarks>
/// <b>The category and a skip are opposites, and this comment used to reject
/// both together.</b> A skip on a missing daemon fails open — CI goes green on
/// a runner whose Docker broke — and that is still refused (§12.4). The trait
/// decides which <i>stage</i> runs a test and never whether it may be absent:
/// selected in, it needs the daemon exactly as before, and selected out it is
/// not reported as passing.
/// <para>
/// It sits on the <b>collection definition</b> rather than on each member
/// class, and that is the whole design. xUnit v3 applies a collection's traits
/// to every test in it — measured on this assembly, where the two filters
/// <i>partition</i> it: every test of every member class is selected by
/// <c>Category=Integration</c>, every other test by
/// <c>Category!=Integration</c>, and none by both or neither. So joining the
/// collection <i>is</i> carrying the category, and there is no per-class
/// attribute for a new test class to forget — which is what the three members
/// beside this file rely on: <c>DistributedLockRedisTests</c>,
/// <c>HybridCacheRedisTests</c> and <c>RedisIdempotencyStoreTests</c>.
/// </para>
/// <para>
/// <b>The split used to be written here as two numbers, and is not any
/// more.</b> It said ten tests of two member classes; PR-28 added the third
/// and made both halves false, and it stayed false through a branch whose own
/// reconciliation pass corrected §12.4 and <c>docs/testing.md</c> — this file
/// was not in that diff, so the site the measurement <i>came from</i> was the
/// one still stating the old one. The partition above is what was actually
/// measured and what the design turns on, and it survives a fourth member
/// class; the counts belong to §12.4 and <c>docs/testing.md</c>, which is one
/// place rather than three. See <c>docs/testing.md</c> for the filters this
/// makes available.
/// </para>
/// </remarks>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<RedisFixture>;
