namespace TiviMateWin.Services;

/// <summary>
/// 免费接口翻译器工厂（v1.4.33 重构）：按配置的服务商建翻译器。
/// 翻译只走免费接口，本地大模型已删掉（用户 10:36：效果不好）。
/// 凭据缺失返回 null（调用方跳过该行，显示英文原文）。参数用原始值（不用 AppConfig），
/// 便于单元测试直接调用。
/// </summary>
public static class FreeApiTranslatorFactory
{
    /// <summary>服务商：微软 Azure（默认）。</summary>
    public const string ProviderAzure = "Azure";

    /// <summary>服务商：Cloudflare Workers AI。</summary>
    public const string ProviderCloudflare = "Cloudflare";

    /// <summary>默认服务商：Azure（免费额度最大：每月 200 万字符）。</summary>
    public const string DefaultProvider = ProviderAzure;

    /// <summary>服务商取值归一化：非法/空值回落 Azure（默认）。</summary>
    public static string NormalizeProvider(string? provider)
    {
        if (string.Equals(provider, ProviderCloudflare, StringComparison.OrdinalIgnoreCase))
            return ProviderCloudflare;
        return ProviderAzure;
    }

    /// <summary>
    /// 按服务商建翻译器（凭据缺失返回 null）：
    /// Azure 用 freeApiKey（还可用 azureRegion）；
    /// Cloudflare 用 cloudflareAccountId + cloudflareApiToken。
    /// </summary>
    public static IFreeApiTranslator? Create(string? provider, string? freeApiKey, string? azureRegion,
        string? cloudflareAccountId, string? cloudflareApiToken)
    {
        switch (NormalizeProvider(provider))
        {
            case ProviderCloudflare:
                if (string.IsNullOrWhiteSpace(cloudflareAccountId) ||
                    string.IsNullOrWhiteSpace(cloudflareApiToken)) return null;
                return new CloudflareTranslator(cloudflareAccountId, cloudflareApiToken);
            default: // Azure
                if (string.IsNullOrWhiteSpace(freeApiKey)) return null;
                return new AzureTranslator(freeApiKey, azureRegion);
        }
    }
}
