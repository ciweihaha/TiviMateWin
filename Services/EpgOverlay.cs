using System;
using System.Collections.Generic;
using System.Linq;
using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>
/// 换台信息条取数（v1.4.33，纯逻辑）：从节目单里挑当前节目 + 接下来 2 个，
/// 供 ShowChannelOsd 显示（左下角小卡片，5 秒自动消失）。
/// </summary>
public static class EpgOverlay
{
    public sealed record Pick(EpgProgram? Current, List<EpgProgram> Next);

    /// <summary>挑当前节目与接下来至多 2 个节目（按开始时间排序后取开始晚于 now 的前 2 个）。</summary>
    public static Pick PickCurrentAndNext(IReadOnlyList<EpgProgram> programs, DateTime now)
    {
        EpgProgram? current = null;
        var next = new List<EpgProgram>();
        foreach (var p in programs.OrderBy(p => p.Start))
        {
            if (p.Start <= now && now < p.Stop) current = p;
            else if (p.Start > now && next.Count < 2) next.Add(p);
        }
        return new Pick(current, next);
    }
}
