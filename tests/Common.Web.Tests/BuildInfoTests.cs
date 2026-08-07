using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class BuildInfoTests
{
    [Theory]
    [InlineData("1.2.3+0a1b2c3", "1.2.3")]          // deterministic build
    [InlineData("1.2.3", "1.2.3")]                  // no source revision
    [InlineData("1.2.3+", "1.2.3")]                 // suffix marker, empty sha
    [InlineData(null, "0.0.0")]                     // no attribute at all
    [InlineData("", "0.0.0")]
    [InlineData("   ", "0.0.0")]
    public void The_informational_version_is_normalised(string? informational, string expected)
    {
        // Asserted through Normalise rather than Version, because a test can
        // choose what goes in. A test against Version alone passes against an
        // implementation that returns the constant "0.0.0" and never reads an
        // assembly at all, which is what this file used to do.
        BuildInfo.Normalise(informational).ShouldBe(expected);
    }

    [Fact]
    public void The_entry_assembly_supplies_a_version()
    {
        // The other half: that Version is wired to a real assembly. The SDK
        // stamps AssemblyInformationalVersion with "<version>+<sha>" for any
        // project inside a git working copy, so under `dotnet test` this runs
        // the strip branch on a genuine input rather than a contrived one.
        BuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
        BuildInfo.Version.ShouldNotContain("+");
    }
}
