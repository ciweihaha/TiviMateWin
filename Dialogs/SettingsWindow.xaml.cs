using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using TiviMateWin.Services;

namespace TiviMateWin.Dialogs;

/// <summary>设置对话框：网络缓存、硬件解码、画面比例、倍速、主题、Xtream 接入、OpenList 网盘</summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private bool _initializing = true;   // 初始化期间不触发事件处理
    private bool _initialHwDecode;   // 第 4 轮 R4-D：打开设置时的硬件解码初始值，用于判断是否改动过
    private string? _latestTag;   // v1.4.35：检查更新发现的新版本号（下载按钮用）
    private bool _checkingUpdate;
    private bool _downloadingUpdate;

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        // v1.4.35：版本一眼能看到（用户不知道自己在用哪个版本）。
        VersionText.Text = "当前版本 " + AppVersion.Display(AppVersion.Current);

        // 网络缓存
        CacheSlider.Value = config.NetworkCache;
        CacheValue.Text = $"{config.NetworkCache} ms";

        // 硬件解码
        HwDecodeChk.IsChecked = config.HardwareDecode;
        _initialHwDecode = config.HardwareDecode;

        // 画面比例
        var aspect = config.AspectRatio ?? "";
        SelectCombo(AspectCombo, aspect switch
        {
            "" or null => "原始",
            "16:9" => "16:9",
            "4:3" => "4:3",
            "拉伸" => "拉伸",
            _ => "原始"
        });

        // 倍速
        var rate = config.PlaybackRate > 0 ? config.PlaybackRate : 1.0;
        SelectCombo(RateCombo, rate switch
        {
            <= 0.5 => "0.5x",
            <= 0.75 => "0.75x",
            <= 1.0 => "1.0x",
            <= 1.25 => "1.25x",
            <= 1.5 => "1.5x",
            _ => "2.0x"
        });

        // 主题
        SelectCombo(ThemeCombo, config.Theme == "浅色" ? "浅色" : "深色");

        // 直播实时字幕（听写+翻译）
        LiveSubtitleChk.IsChecked = config.LiveSubtitleEnabled;
        LiveSubtitleEnglishChk.IsChecked = config.LiveSubtitleShowEnglish;
        LiveSubtitleSerialChk.IsChecked = config.LiveSubtitleSerial;
        SelectCombo(LiveSubtitleModeCombo, config.LiveSubtitleTranslationMode switch
        {
            "Off" => "关闭",
            "All" => "全部台",
            _ => "仅英语台",
        });
        // v1.4.33 重构：翻译服务商（微软 Azure / Cloudflare）+ 免费接口凭据。
        SelectCombo(LiveSubtitleProviderCombo, config.FreeApiProvider switch
        {
            "Cloudflare" => "Cloudflare（免费）",
            _ => "微软 Azure（免费）",
        });
        FreeApiKeyBox.Password = config.FreeApiKey ?? "";
        AzureRegionBox.Text = config.AzureRegion ?? "";
        CloudflareAccountIdBox.Text = config.CloudflareAccountId ?? "";
        CloudflareApiTokenBox.Password = config.CloudflareApiToken ?? "";

        // Xtream
        XtreamServerBox.Text = config.XtreamServer ?? "";
        XtreamUserBox.Text = config.XtreamUsername ?? "";
        XtreamPassBox.Password = config.XtreamPassword ?? "";

        // OpenList 网盘
        OpenListServerBox.Text = config.OpenListServer ?? "";
        OpenListUserBox.Text = config.OpenListUsername ?? "";
        OpenListPassBox.Password = config.OpenListPassword ?? "";

        _initializing = false;
    }

    private static void SelectCombo(ComboBox combo, string content)
    {
        foreach (ComboBoxItem item in combo.Items)
        {
            if (item.Content?.ToString() == content)
            {
                combo.SelectedItem = item;
                break;
            }
        }
    }

    private void CacheSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing) return;
        CacheValue.Text = $"{(int)CacheSlider.Value} ms";
    }

    /// <summary>检查更新（v1.4.35）：去 GitHub 查最新 release，和当前版本比。
    /// 已是最新就明说“已是最新版本”；有新版就显示版本号 + “下载新版本”按钮。</summary>
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_checkingUpdate) return;
        _checkingUpdate = true;
        CheckUpdateBtn.IsEnabled = false;
        UpdateResultText.Text = "正在检查更新…";
        DownloadUpdateBtn.Visibility = Visibility.Collapsed;
        _latestTag = null;
        try
        {
            var current = AppVersion.Display(AppVersion.Current);
            var result = await UpdateChecker.CheckAsync(current);
            if (!IsLoaded) return;
            if (result.Error != null)
            {
                UpdateResultText.Text = result.Error;
            }
            else if (result.IsUpdateAvailable)
            {
                _latestTag = result.LatestTag;
                UpdateResultText.Text = $"发现新版本 {result.LatestTag}（当前 {current}），点“下载新版本”下载安装包。";
                DownloadUpdateBtn.Visibility = Visibility.Visible;
            }
            else
            {
                UpdateResultText.Text = $"已是最新版本（{current}）。";
            }
        }
        finally
        {
            _checkingUpdate = false;
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    /// <summary>下载新版本安装包（v1.4.35）：走国内镜像（ghfast → gh-proxy → 直链），
    /// 存在“下载”文件夹；程序运行时不能覆盖自己的文件，所以只下载、不自动安装，
    /// 下载完告诉用户关闭程序后解压覆盖（先把旧版文件夹改名备份）。</summary>
    private async void DownloadUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_downloadingUpdate || _latestTag is null) return;
        _downloadingUpdate = true;
        DownloadUpdateBtn.IsEnabled = false;
        CheckUpdateBtn.IsEnabled = false;
        var tag = _latestTag;
        var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        var dest = Path.Combine(downloads, $"TiviMateWin-{tag}-win-x64.zip");
        UpdateResultText.Text = $"正在下载新版本 {tag}（约 152MB，请稍等）…";
        try
        {
            Directory.CreateDirectory(downloads);
            var ok = await UpdateChecker.DownloadAsync(tag, dest);
            if (!IsLoaded) return;
            UpdateResultText.Text = ok
                ? $"已下载到 {dest}。关闭本程序后解压覆盖安装即可（先把旧版文件夹改名备份）。"
                : "下载失败（网络不通），稍后再试，或去 GitHub 发布页手动下载。";
        }
        finally
        {
            _downloadingUpdate = false;
            DownloadUpdateBtn.IsEnabled = true;
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private void HwDecode_Changed(object sender, RoutedEventArgs e)
    {
        // 第 4 轮 R4-D：硬件解码只在下次创建 LibVLC 实例（即重启应用）时生效，
        // 旧代码的注释写了“需重启生效”但界面上没有任何提示；勾选值与打开设置时的
        // 初始值不一致时显示提示，改回去则隐藏。
        if (_initializing) return;
        HwDecodeHint.Visibility = (HwDecodeChk.IsChecked == true) != _initialHwDecode
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Aspect_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 保存时读取
    }

    private void Rate_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 保存时读取
    }

    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    {
        // 保存时读取
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _config.NetworkCache = (int)CacheSlider.Value;
        _config.HardwareDecode = HwDecodeChk.IsChecked == true;

        _config.AspectRatio = (AspectCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "16:9" => "16:9",
            "4:3" => "4:3",
            "拉伸" => "拉伸",
            _ => ""
        };

        _config.PlaybackRate = (RateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "0.5x" => 0.5,
            "0.75x" => 0.75,
            "1.25x" => 1.25,
            "1.5x" => 1.5,
            "2.0x" => 2.0,
            _ => 1.0
        };

        _config.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "深色";

        // 直播实时字幕（听写+翻译）
        _config.LiveSubtitleEnabled = LiveSubtitleChk.IsChecked == true;
        _config.LiveSubtitleShowEnglish = LiveSubtitleEnglishChk.IsChecked == true;
        _config.LiveSubtitleSerial = LiveSubtitleSerialChk.IsChecked == true;
        _config.LiveSubtitleTranslationMode = (LiveSubtitleModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "关闭" => "Off",
            "全部台" => "All",
            _ => "EnglishOnly",
        };
        // v1.4.33 重构：翻译服务商 + 免费接口凭据（Key/令牌经 AppConfig.Save DPAPI 加密落盘）。
        _config.FreeApiProvider =
            (LiveSubtitleProviderCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
            {
                "Cloudflare（免费）" => "Cloudflare",
                _ => "Azure",
            };
        _config.FreeApiKey = FreeApiKeyBox.Password;
        _config.AzureRegion = AzureRegionBox.Text.Trim();
        _config.CloudflareAccountId = CloudflareAccountIdBox.Text.Trim();
        _config.CloudflareApiToken = CloudflareApiTokenBox.Password;

        var server = XtreamServerBox.Text.Trim();
        _config.XtreamServer = server;
        _config.XtreamUsername = XtreamUserBox.Text.Trim();
        _config.XtreamPassword = XtreamPassBox.Password;

        var olServer = OpenListServerBox.Text.Trim();
        _config.OpenListServer = olServer;
        _config.OpenListUsername = OpenListUserBox.Text.Trim();
        _config.OpenListPassword = OpenListPassBox.Password;

        // L13：Xtream 凭据放 query 参数是协议要求，无法避免；但服务器是 http:// 时凭据明文传输，
        // 在保存时一次性提示风险（不拦截保存），让用户有意识地选择。
        // 第 4 轮 R4-C：OpenList 登录同样把用户名/密码以 JSON 明文 POST 到 http:// 服务器，
        // 暴露面与 Xtream 相同，之前只提示了 Xtream——两个都提示，合并成一条。
        var plainServers = new List<string>();
        if (server.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) plainServers.Add("Xtream");
        if (olServer.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) plainServers.Add("OpenList 网盘");
        if (plainServers.Count > 0)
        {
            MessageBox.Show(this,
                $"{string.Join("、", plainServers)}服务器使用 http:// 明文连接，登录凭据（用户名/密码）将以明文传输，同一网络下的他人可能窃取。\n建议改用 https:// 服务器地址。",
                "明文传输风险", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // v1.4.15 迭代 3：配置保存放后台线程（DPAPI 加密 + 原子换名，慢盘会卡 UI）；
        // 读盘失败待确认时仍走 UI 线程（Save 里要弹确认框）。
        if (AppConfig.ReadFailurePending) _config.Save();
        else _ = Task.Run(() => _config.Save());
        DialogResult = true;
    }
}
