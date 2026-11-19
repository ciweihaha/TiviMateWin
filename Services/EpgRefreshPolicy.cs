using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>EPG 自动重抓判定（v1.4.32，纯逻辑，无 WPF 依赖，可单测）：
/// 服务端节目单每天更新，但 app 以前只在加载播放列表时抓一次，之后每分钟定时器
/// 只重绘本地显示、从不重抓，节目单会停在昨天。满足任一条件就重抓：缓存已超
/// 1 小时；或当前频道节目已过期。5 分钟内不重复发起（失败后 tick/回前台不会刷屏重试）。</summary>
public static class EpgRefreshPolicy
{
    public static bool ShouldRefetchEpg(DateTime? loadedAt, DateTime lastAttemptAt, DateTime now, bool dataExpired)
    {
        if (loadedAt is null) return false;   // 从未成功抓到过：首抓走播放列表加载路径，这里不碰
        if (now - lastAttemptAt < TimeSpan.FromMinutes(5)) return false;
        if (dataExpired) return true;
        return now - loadedAt.Value >= TimeSpan.FromHours(1);
    }

    /// <summary>当前频道缓存节目是否已过期：当前时间已越过该频道最后一条节目的
    /// 结束时间。节目表为空不算过期（该频道本来就没节目单，重抓也拿不到东西）；
    /// 节目之间有空档（当前无节目、但后面还有）也不算过期。</summary>
    public static bool EpgDataExpired(IReadOnlyList<EpgProgram> programs, DateTime now)
    {
        if (programs.Count == 0) return false;
        var lastStop = programs[0].Stop;
        foreach (var p in programs)
            if (p.Stop > lastStop) lastStop = p.Stop;
        return now >= lastStop;
    }
}
