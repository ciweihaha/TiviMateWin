using System;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>版本号显示（v1.4.35）。</summary>
public class AppVersionTests
{
    [Fact]
    public void Display_TrimsRevision()
    {
        Assert.Equal("1.4.35", AppVersion.Display(new Version(1, 4, 35, 0)));
    }

    [Fact]
    public void Display_Null_ReturnsQuestionMark()
    {
        Assert.Equal("?", AppVersion.Display(null));
    }

    [Fact]
    public void Display_TwoPartVersion()
    {
        Assert.Equal("1.4.0", AppVersion.Display(new Version(1, 4)));
    }
}
