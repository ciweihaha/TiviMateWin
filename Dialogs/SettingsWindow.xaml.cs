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

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

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
        LiveSubtitleModelBox.Text = config.LiveSubtitleModel ?? "";

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
        var subtitleModel = LiveSubtitleModelBox.Text.Trim();
        _config.LiveSubtitleModel = subtitleModel.Length > 0
            ? subtitleModel : "qwen2.5-1.5b-instruct-q4_k_m.gguf";

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
