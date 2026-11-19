using TiviMateWin.Services;
using Xunit;

namespace TiviMateWin.Tests;

/// <summary>翻译强制开关切换测试（v1.4.34）：自动 → 强制开 → 强制关 → 自动；
/// 点到"强制开"时总开关必须一起开，否则 ResolveTranslationActive 直接返回 false，
/// 按钮文案变了、字幕却永远不出——这就是台式机上"翻译开关打不开"的根因。</summary>
public class TranslationToggleTests
{
    [Fact]
    public void Cycle_MasterOff_AutoToForcedOn_TurnsMasterOn()
    {
        // 核心回归：总开关关着时点"强制开"，总开关要跟着开，否则开关永远打不开。
        var (master, force) = TranslationToggle.Cycle(false, null);
        Assert.True(master);
        Assert.True(force);
    }

    [Fact]
    public void Cycle_MasterOn_AutoToForcedOn_KeepsMasterOn()
    {
        var (master, force) = TranslationToggle.Cycle(true, null);
        Assert.True(master);
        Assert.True(force);
    }

    [Fact]
    public void Cycle_ForcedOn_ToForcedOff_KeepsMaster()
    {
        var (master, force) = TranslationToggle.Cycle(true, true);
        Assert.True(master);
        Assert.False(force);
    }

    [Fact]
    public void Cycle_ForcedOff_ToAuto_KeepsMaster()
    {
        var (master, force) = TranslationToggle.Cycle(true, false);
        Assert.True(master);
        Assert.Null(force);
    }

    [Fact]
    public void Cycle_FullRound_ReturnsToAuto()
    {
        var (m1, f1) = TranslationToggle.Cycle(false, null);
        var (m2, f2) = TranslationToggle.Cycle(m1, f1);
        var (m3, f3) = TranslationToggle.Cycle(m2, f2);
        Assert.True(m1);
        Assert.True(f1);
        Assert.True(m2);
        Assert.False(f2);
        Assert.True(m3);
        Assert.Null(f3);
    }
}
