using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>检查更新（v1.4.35）：解析、比大小、下载地址三条规则。</summary>
public class UpdateCheckerTests
{
    [Fact]
    public void ParseLatestTag_ReadsTagName()
    {
        Assert.Equal("v1.4.36", UpdateChecker.ParseLatestTag("{\"tag_name\":\"v1.4.36\",\"name\":\"x\"}"));
    }

    [Fact]
    public void ParseLatestTag_BadJson_ReturnsNull()
    {
        Assert.Null(UpdateChecker.ParseLatestTag("not json"));
        Assert.Null(UpdateChecker.ParseLatestTag("{}"));
        Assert.Null(UpdateChecker.ParseLatestTag("{\"tag_name\":\"\"}"));
    }

    [Fact]
    public void IsNewer_NewerTag_ReturnsTrue()
    {
        Assert.True(UpdateChecker.IsNewer("1.4.35", "v1.4.36"));
        Assert.True(UpdateChecker.IsNewer("1.4.35", "v1.5.0"));
        Assert.True(UpdateChecker.IsNewer("1.4.35", "v2.0.0"));
    }

    [Fact]
    public void IsNewer_SameOrOlder_ReturnsFalse()
    {
        Assert.False(UpdateChecker.IsNewer("1.4.35", "v1.4.35"));
        Assert.False(UpdateChecker.IsNewer("1.4.35", "v1.4.34"));
        Assert.False(UpdateChecker.IsNewer("1.4.35", "1.4.35"));
    }

    [Fact]
    public void IsNewer_BadInput_ReturnsFalse()
    {
        Assert.False(UpdateChecker.IsNewer("1.4.35", null));
        Assert.False(UpdateChecker.IsNewer("1.4.35", ""));
        Assert.False(UpdateChecker.IsNewer("1.4.35", "vNext"));
        Assert.False(UpdateChecker.IsNewer("abc", "v1.4.36"));
    }

    [Fact]
    public void DownloadUrls_BuildsAllThreeMirrors()
    {
        var urls = UpdateChecker.DownloadUrls("v1.4.36");
        Assert.Equal("https://github.com/ciweihaha/TiviMateWin/releases/download/v1.4.36/TiviMateWin-win-x64.zip", urls.Direct);
        Assert.Equal("https://ghfast.top/https://github.com/ciweihaha/TiviMateWin/releases/download/v1.4.36/TiviMateWin-win-x64.zip", urls.GhFast);
        Assert.Equal("https://gh-proxy.com/https://github.com/ciweihaha/TiviMateWin/releases/download/v1.4.36/TiviMateWin-win-x64.zip", urls.GhProxy);
    }

    [Fact]
    public void ApiUrl_PointsAtThisRepo()
    {
        Assert.Equal("https://api.github.com/repos/ciweihaha/TiviMateWin/releases/latest", UpdateChecker.ApiUrl);
        Assert.Equal("TiviMateWin-win-x64.zip", UpdateChecker.AssetName);
    }
}
