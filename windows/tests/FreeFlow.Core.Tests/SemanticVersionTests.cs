using FreeFlow.Core.Updates;
using Xunit;

namespace FreeFlow.Core.Tests;

/// <summary>Ported from <c>Tests/SemanticVersionTests.swift</c>.</summary>
public class SemanticVersionTests
{
    private static SemanticVersion Version(string value)
    {
        var parsed = SemanticVersion.TryParse(value);
        Assert.NotNull(parsed);
        return parsed!;
    }

    [Fact]
    public void CoreVersionOrdering()
    {
        Assert.True(Version("1.2.3") < Version("1.2.4"), "Patch versions should order numerically");
        Assert.True(Version("1.2.9") < Version("1.3.0"), "Minor versions should order numerically");
        Assert.True(Version("1.9.9") < Version("2.0.0"), "Major versions should order numerically");
    }

    [Fact]
    public void ParsingAndBuildMetadata()
    {
        Assert.Equal(Version(" v1.2.3 "), Version("V1.2.3"));
        // Build metadata is excluded from precedence.
        Assert.Equal(Version("1.2.3+build.1"), Version("1.2.3+build.2"));
        Assert.True(Version("1.2.3-alpha") < Version("1.2.3"),
            "Prereleases must sort before stable releases");
        Assert.True(Version("1.2.3-1") < Version("1.2.3-alpha"),
            "Numeric identifiers must sort before alphanumeric identifiers");
        Assert.True(Version("1.2.3-alpha") < Version("1.2.3-alpha.1"),
            "A shorter matching prerelease must sort first");
    }

    [Fact]
    public void OfficialPrereleaseOrdering()
    {
        // The precedence example straight from the semver 2.0 specification.
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0",
        };

        for (var index = 0; index < ordered.Length - 1; index++)
        {
            Assert.True(
                Version(ordered[index]) < Version(ordered[index + 1]),
                $"Expected {ordered[index]} to sort before {ordered[index + 1]}");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("one.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-alpha..1")]
    public void InvalidVersionsAreRejected(string value)
        => Assert.Null(SemanticVersion.TryParse(value));

    [Fact]
    public void RoundTripsThroughToString()
    {
        Assert.Equal("1.2.3", Version("v1.2.3+build").ToString());
        Assert.Equal("1.2.3-rc.1", Version("1.2.3-rc.1").ToString());
    }
}
