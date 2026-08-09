using Xunit;

namespace Common.Infrastructure.Tests;

/// <summary>
/// §12.4's per-assembly declaration, on the third assembly that needs
/// Docker — and under the first two's policy: no skip and no category when
/// the daemon is absent, because a skip on a missing daemon fails open.
/// </summary>
[CollectionDefinition(nameof(IntegrationCollection))]
public sealed class IntegrationCollection : ICollectionFixture<RedisFixture>;
