using TiviMateWin.Models;

namespace TiviMateWin.Services;

/// <summary>
/// 数字键换台的纯逻辑部分（V1.4 新增，便于 SelfTest 无界面验证）：
/// 输入缓冲长度限制、按频道号精确匹配、是否存在更长号码（决定是否可立即提交）。
/// UI 层只负责按键转发与 OSD 展示，不掺杂匹配规则。
/// </summary>
public static class DigitTuner
{
    public const int MaxDigits = 4;   // 频道号上限 9999，与列表规模匹配

    /// <summary>追加一位数字；超出上限丢弃最早一位（保留最近 MaxDigits 位），非数字字符原样返回。</summary>
    public static string AppendDigit(string buf, char d)
    {
        if (d < '0' || d > '9') return buf;
        buf += d;
        return buf.Length > MaxDigits ? buf[^MaxDigits..] : buf;
    }

    /// <summary>按频道号精确匹配；缓冲非纯数字或越界时返回 null。</summary>
    public static ChannelItem? Match(IEnumerable<ChannelItem> list, string buf)
    {
        if (buf.Length == 0 || !int.TryParse(buf, out var n)) return null;
        return list.FirstOrDefault(c => c.Number == n);
    }

    /// <summary>是否存在以当前输入为前缀的更大号码（如输入 "1" 时存在 ≥10 号）。
    /// 存在则不能立即提交（用户可能还想输入下一位），须等待超时。
    /// 深查第 1 轮：旧实现用 (n+1)*10 作下界，输入 "12" 时下界 130，
    /// 把已存在的 125 漏掉，误判为可立即提交。现改为字符串前缀判定：
    /// 号码文本以 buf 开头且位数更多，即为更长号码。</summary>
    public static bool HasLongerNumber(IEnumerable<ChannelItem> list, string buf)
    {
        if (buf.Length == 0 || !int.TryParse(buf, out _)) return false;
        return list.Any(c => c.Number.ToString().StartsWith(buf, StringComparison.Ordinal)
            && c.Number.ToString().Length > buf.Length);
    }

    /// <summary>是否可立即提交（存在精确匹配且不存在更长号码）。</summary>
    public static bool ShouldCommit(IEnumerable<ChannelItem> list, string buf)
        => Match(list, buf) != null && !HasLongerNumber(list, buf);
}
