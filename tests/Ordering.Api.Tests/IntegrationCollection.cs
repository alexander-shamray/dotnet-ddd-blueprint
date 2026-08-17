using Ordering.TestSupport;
using Xunit;

namespace Ordering.Api.Tests;

/// <summary>
/// §12.4's per-assembly declaration: xUnit resolves collections within an
/// assembly, so each consuming test project declares its own over the shared
/// <see cref="ServiceFixture"/>. Two assemblies mean two container sets per
/// run — the stated price of the pyramid's levels mapping onto projects.
/// </summary>
[CollectionDefinition(nameof(IntegrationCollection))]
public sealed class IntegrationCollection : ICollectionFixture<ServiceFixture>;
