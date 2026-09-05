using Xunit;

namespace System.Drawing.Tests;

public sealed class AssemblyIdentityTests
{
    [Fact]
    public void AssemblyVersion_MatchesNet10ContractMajorMinor()
    {
        Version? version = typeof(Graphics).Assembly.GetName().Version;

        Assert.NotNull(version);
        Assert.Equal(new Version(10, 0, 0, 0), version);
    }
}
