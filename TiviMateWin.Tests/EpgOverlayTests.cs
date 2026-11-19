using System;
using System.Collections.Generic;
using TiviMateWin.Models;
using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>换台信息条取数测试（v1.4.33）：左下角小卡片要显示当前节目（含时间段）+
/// 接下来 2 个节目。无节目/无当前节目/只剩 1 个预告时不崩。</summary>
public class EpgOverlayTests
{
    private static List<EpgProgram> Programs(params (string title, int startHour, int stopHour)[] ranges)
    {
        var day = new DateTime(2026, 11, 19, 0, 0, 0);
        var list = new List<EpgProgram>();
        foreach (var (title, s, e) in ranges)
            list.Add(new EpgProgram { Title = title, Start = day.AddHours(s), Stop = day.AddHours(e) });
        return list;
    }

    [Fact]
    public void Pick_ReturnsCurrentAndNextTwo()
    {
        var progs = Programs(
            ("早间新闻", 6, 9),
            ("午间新闻", 12, 13),
            ("晚间新闻", 19, 20),
            ("焦点访谈", 20, 21));
        var now = new DateTime(2026, 11, 19, 12, 30, 0);
        var pick = EpgOverlay.PickCurrentAndNext(progs, now);
        Assert.Equal("午间新闻", pick.Current?.Title);
        Assert.Equal(2, pick.Next.Count);
        Assert.Equal("晚间新闻", pick.Next[0].Title);
        Assert.Equal("焦点访谈", pick.Next[1].Title);
    }

    [Fact]
    public void Pick_NoCurrent_ReturnsNullCurrentWithNext()
    {
        var progs = Programs(("晚间新闻", 19, 20), ("焦点访谈", 20, 21));
        var pick = EpgOverlay.PickCurrentAndNext(progs, new DateTime(2026, 11, 19, 12, 30, 0));
        Assert.Null(pick.Current);
        Assert.Equal(2, pick.Next.Count);
        Assert.Equal("晚间新闻", pick.Next[0].Title);
    }

    [Fact]
    public void Pick_OnlyOneNext_ReturnsOne()
    {
        var progs = Programs(("午间新闻", 12, 13), ("晚间新闻", 19, 20));
        var pick = EpgOverlay.PickCurrentAndNext(progs, new DateTime(2026, 11, 19, 12, 30, 0));
        Assert.Equal("午间新闻", pick.Current?.Title);
        Assert.Single(pick.Next);
    }

    [Fact]
    public void Pick_EmptyPrograms_ReturnsEmpty()
    {
        var pick = EpgOverlay.PickCurrentAndNext(new List<EpgProgram>(), new DateTime(2026, 11, 19, 12, 30, 0));
        Assert.Null(pick.Current);
        Assert.Empty(pick.Next);
    }

    [Fact]
    public void Pick_UnsortedInput_SortedByStart()
    {
        var progs = Programs(
            ("焦点访谈", 20, 21),
            ("早间新闻", 6, 9),
            ("晚间新闻", 19, 20));
        var pick = EpgOverlay.PickCurrentAndNext(progs, new DateTime(2026, 11, 19, 7, 0, 0));
        Assert.Equal("早间新闻", pick.Current?.Title);
        Assert.Equal("晚间新闻", pick.Next[0].Title);
        Assert.Equal("焦点访谈", pick.Next[1].Title);
    }
}
