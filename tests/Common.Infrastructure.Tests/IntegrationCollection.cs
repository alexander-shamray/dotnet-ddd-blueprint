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
/// to every test in it — measured on this assembly, where
/// <c>Category=Integration</c> selects all ten tests of the two member classes
/// and <c>Category!=Integration</c> selects the other seventy-one, with no
/// third state. So joining the collection <i>is</i> carrying the category, and
/// there is no per-class attribute for a new test class to forget. See
/// <c>docs/testing.md</c> for the filters this makes available.
/// </para>
/// </remarks>
[CollectionDefinition(nameof(IntegrationCollection))]
[Trait("Category", "Integration")]
public sealed class IntegrationCollection : ICollectionFixture<RedisFixture>;
