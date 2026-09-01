using FreeFlow.Core.Transcription;
using FreeFlow.Core.Updates;
using Xunit;

namespace FreeFlow.Core.Tests;

public class UpdateCheckerTests
{
    private const string Releases = """
    [
      { "tag_name": "v1.0.0", "draft": false, "prerelease": false, "html_url": "https://example.test/1",
        "body": "Stable", "assets": [{ "browser_download_url": "https://example.test/FreeFlow-1.0.0.msi" }] },
      { "tag_name": "v1.2.0", "draft": false, "prerelease": false, "html_url": "https://example.test/2",
        "body": "Newer", "assets": [{ "browser_download_url": "https://example.test/FreeFlow-1.2.0.msi" }] },
      { "tag_name": "v2.0.0-rc.1", "draft": false, "prerelease": true, "html_url": "https://example.test/3",
        "body": "Candidate", "assets": [] },
      { "tag_name": "v3.0.0", "draft": true, "prerelease": false, "html_url": "https://example.test/4",
        "body": "Unpublished", "assets": [] }
    ]
    """;

    private static SemanticVersion Version(string value) => SemanticVersion.TryParse(value)!;

    [Fact]
    public void PicksTheHighestStableRelease()
    {
        var update = UpdateChecker.SelectNewest(Releases, Version("1.0.0"), false, ".msi");

        Assert.NotNull(update);
        Assert.Equal("v1.2.0", update!.TagName);
        Assert.Equal("https://example.test/FreeFlow-1.2.0.msi", update.DownloadUrl);
    }

    [Fact]
    public void SkipsDraftsAndPrereleasesByDefault()
    {
        var update = UpdateChecker.SelectNewest(Releases, Version("1.2.0"), false, ".msi");
        // Only a draft and a prerelease sit above 1.2.0, so nothing should be offered.
        Assert.Null(update);
    }

    [Fact]
    public void IncludesPrereleasesWhenAsked()
    {
        var update = UpdateChecker.SelectNewest(Releases, Version("1.2.0"), true, ".msi");

        Assert.NotNull(update);
        Assert.Equal("v2.0.0-rc.1", update!.TagName);
        // A release with no matching asset still reports, so the user can open the page.
        Assert.Null(update.DownloadUrl);
    }

    [Fact]
    public void DraftsAreNeverOffered()
    {
        var update = UpdateChecker.SelectNewest(Releases, Version("2.0.0"), true, ".msi");
        Assert.Null(update);
    }

    [Fact]
    public void MalformedPayloadIsIgnored()
    {
        Assert.Null(UpdateChecker.SelectNewest("not json", Version("1.0.0"), false, ".msi"));
        Assert.Null(UpdateChecker.SelectNewest("{}", Version("1.0.0"), false, ".msi"));
    }
}

public class RealtimeUrlTests
{
    [Theory]
    [InlineData("https://api.groq.com/openai/v1", "wss://api.groq.com/openai/v1/realtime?intent=transcription")]
    [InlineData("https://api.groq.com/openai", "wss://api.groq.com/openai/v1/realtime?intent=transcription")]
    [InlineData("https://api.groq.com/openai/v1/", "wss://api.groq.com/openai/v1/realtime?intent=transcription")]
    [InlineData("http://localhost:11434/v1", "ws://localhost:11434/v1/realtime?intent=transcription")]
    public void DerivesTheRealtimeSocketUrl(string baseUrl, string expected)
        => Assert.Equal(expected, RealtimeTranscriptionService.DeriveWebSocketUri(baseUrl)?.ToString());

    [Theory]
    [InlineData("ftp://example.com/v1")]
    [InlineData("not a url")]
    public void RejectsUnusableBaseUrls(string baseUrl)
        => Assert.Null(RealtimeTranscriptionService.DeriveWebSocketUri(baseUrl));
}
