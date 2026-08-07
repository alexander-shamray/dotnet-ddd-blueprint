using Shouldly;
using Xunit;

namespace Common.Web.Tests;

public class BuildInfoTests
{
    [Fact]
    public void A_version_is_always_produced()
    {
        BuildInfo.Version.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public void The_source_revision_suffix_is_stripped()
    {
        // A deterministic build stamps AssemblyInformationalVersion as
        // "1.2.3+<sha>". The sha belongs in neither service.version nor any
        // dashboard grouping by it — it would make every rebuild a new series.
        BuildInfo.Version.ShouldNotContain("+");
    }
}
