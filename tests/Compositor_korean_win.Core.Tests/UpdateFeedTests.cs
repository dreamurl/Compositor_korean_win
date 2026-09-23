using Compositor_korean_win.Core;
using Xunit;

namespace Compositor_korean_win.Core.Tests;

/// <summary>The update feed: what it has to look like, and when it means there is something newer.</summary>
public class UpdateFeedTests
{
    [Fact]
    public void AFeedGivesItsVersionAndPage()
    {
        UpdateInfo info = UpdateFeed.Parse("""{"version": "v1.2.0", "page": "https://github.com/a/b/releases/tag/v1.2.0"}""")!;

        Assert.Equal(new Version(1, 2, 0), info.Version);
        Assert.Equal("https://github.com/a/b/releases/tag/v1.2.0", info.Page);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"version": "1.0.0"}""")]
    [InlineData("""{"version": "one", "page": "https://example.com"}""")]
    [InlineData("""{"version": "1.0.0", "page": "file:///C:/Windows/System32/calc.exe"}""")]
    [InlineData("""{"version": "1.0.0", "page": "http://example.com"}""")]
    public void AnythingElseIsNotAFeed(string json) => Assert.Null(UpdateFeed.Parse(json));

    [Theory]
    [InlineData("1.0.0", "1.0.1", true)]
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.2.0", "1.1.9", false)]
    [InlineData("1.2", "1.2.0", false)]
    [InlineData("0.0.0-dev", "1.0.0", true)]
    [InlineData("1.0.0+abcdef", "1.0.0", false)]
    [InlineData("nonsense", "1.0.0", true)]
    public void NewerIsByVersion(string running, string released, bool newer)
    {
        var latest = new UpdateInfo(UpdateFeed.ParseVersion(released)!, "https://example.com");
        Assert.Equal(newer, UpdateFeed.IsNewer(latest, running));
    }
}
