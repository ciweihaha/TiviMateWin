using System;
using TiviMateWin.Services;
using System.Collections.Generic;
using TiviMateWin.Models;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>EPG 自动重抓判定测试（v1.4.32）：服务端节目单每天更新，app 以前只在加载
/// 播放列表时抓一次，之后只重绘本地显示、从不重抓，节目单会停在昨天。判定须覆盖：
/// 每小时重抓、数据过期立即重抓、5 分钟内不重复发起、从未加载过不触发。</summary>
public class EpgAutoRefreshTests
{
    private static List<EpgProgram> Programs(params (int startHour, int stopHour)[] ranges)
    {
        var day = new DateTime(2026, 11, 19, 0, 0, 0);
        var list = new List<EpgProgram>();
        foreach (var (s, e) in ranges)
            list.Add(new EpgProgram { Start = day.AddHours(s), Stop = day.AddHours(e), Title = "节目" });
        return list;
    }

    [Fact]
    public void HourlyRefresh_DueAfterOneHour()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.True(EpgRefreshPolicy.ShouldRefetchEpg(now.AddHours(-1), now.AddHours(-1), now, dataExpired: false));
    }

    [Fact]
    public void HourlyRefresh_NotDueWithinOneHour()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.False(EpgRefreshPolicy.ShouldRefetchEpg(now.AddMinutes(-59), now.AddHours(-1), now, dataExpired: false));
    }

    [Fact]
    public void ExpiredData_RefetchesEvenWithinOneHour()
    {
        // 昨晚 20:04 停住的节目单：加载才 10 分钟，但当前频道节目已过期，仍须重抓。
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.True(EpgRefreshPolicy.ShouldRefetchEpg(now.AddMinutes(-10), now.AddMinutes(-10), now, dataExpired: true));
    }

    [Fact]
    public void RecentAttempt_ThrottlesRepeatWithinFiveMinutes()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.False(EpgRefreshPolicy.ShouldRefetchEpg(now.AddHours(-2), now.AddMinutes(-4), now, dataExpired: true));
        Assert.True(EpgRefreshPolicy.ShouldRefetchEpg(now.AddHours(-2), now.AddMinutes(-5), now, dataExpired: true));
    }

    [Fact]
    public void NeverLoaded_DoesNotRefetch()
    {
        var now = new DateTime(2026, 11, 19, 10, 0, 0);
        Assert.False(EpgRefreshPolicy.ShouldRefetchEpg(null, DateTime.MinValue, now, dataExpired: true));
    }

    [Fact]
    public void DataExpired_NowPastLastStop()
    {
        var progs = Programs((0, 12), (12, 20));
        Assert.True(EpgRefreshPolicy.EpgDataExpired(progs, new DateTime(2026, 11, 19, 20, 4, 0)));
    }

    [Fact]
    public void DataExpired_NotExpiredWhileProgramRunning()
    {
        var progs = Programs((0, 12), (12, 23));
        Assert.False(EpgRefreshPolicy.EpgDataExpired(progs, new DateTime(2026, 11, 19, 20, 4, 0)));
    }

    [Fact]
    public void DataExpired_GapBetweenProgramsIsNotExpired()
    {
        // 节目之间有空档：当前无节目、但后面还有节目，不算过期，不重抓。
        var progs = Programs((0, 12), (14, 23));
        Assert.False(EpgRefreshPolicy.EpgDataExpired(progs, new DateTime(2026, 11, 19, 13, 0, 0)));
    }

    [Fact]
    public void DataExpired_EmptyProgramListIsNotExpired()
    {
        Assert.False(EpgRefreshPolicy.EpgDataExpired(new List<EpgProgram>(), new DateTime(2026, 11, 19, 20, 4, 0)));
    }
}
