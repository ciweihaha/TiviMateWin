using System.Windows;
using System.Windows.Controls;
using TiviMateWin.Services;

namespace TiviMateWin.Dialogs;

/// <summary>设置对话框：网络缓存、硬件解码、画面比例、倍速、主题、Xtream 接入</summary>
public partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private bool _initializing = true;   // 初始化期间不触发事件处理

    public SettingsWindow(AppConfig config)
    {
        InitializeComponent();
        _config = config;

        // 网络缓存
        CacheSlider.Value = config.NetworkCache;
        CacheValue.Text = $"{config.NetworkCache} ms";

        // 硬件解码
        HwDecodeChk.IsChecked = config.HardwareDecode;

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

        // Xtream
        XtreamServerBox.Text = config.XtreamServer ?? "";
        XtreamUserBox.Text = config.XtreamUsername ?? "";
        XtreamPassBox.Password = config.XtreamPassword ?? "";

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
        // 实时提示：硬件解码需重启生效
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

        _config.XtreamServer = XtreamServerBox.Text.Trim();
        _config.XtreamUsername = XtreamUserBox.Text.Trim();
        _config.XtreamPassword = XtreamPassBox.Password;

        _config.Save();
        DialogResult = true;
    }
}
