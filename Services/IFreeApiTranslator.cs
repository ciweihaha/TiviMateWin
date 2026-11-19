namespace TiviMateWin.Services;

/// <summary>
/// 免费接口翻译器（v1.4.33 重构）：翻译只走免费接口（微软 Azure / 谷歌 / Cloudflare），
/// 本地大模型已删掉（用户 10:36：效果不好，不用了）。任何失败返回 null（不抛异常），
/// 调用方只跳过该行（显示英文原文），不再回落本地。听写（Whisper tiny.en）仍在本地。
/// 实现类须自带短句缓存（重复台词不重复调接口，省额度也省延迟）。
/// </summary>
public interface IFreeApiTranslator : IDisposable
{
    /// <summary>服务商名（日志与设置界面显示，如 Azure / Google / Cloudflare）。</summary>
    string ProviderName { get; }

    /// <summary>英翻中：空文本返回 ""，含中文返回原文；失败返回 null（不抛异常）。</summary>
    Task<string?> TranslateAsync(string english, CancellationToken ct);
}
