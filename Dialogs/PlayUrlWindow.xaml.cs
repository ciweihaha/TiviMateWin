using System.Windows;
using System.Windows.Controls;

namespace TiviMateWin.Dialogs;

/// <summary>点播链接输入：直链播放（主窗口/小窗）或转到点播源导入</summary>
public partial class PlayUrlWindow : Window
{
    public enum PlayUrlChoice { None, Main, Pip, ImportVod }

    public PlayUrlChoice Choice { get; private set; } = PlayUrlChoice.None;
    public string PlayUrl => UrlBox.Text.Trim();
    public string PlayName => NameBox.Text.Trim();

    public PlayUrlWindow(List<string> recentUrls)
    {
        InitializeComponent();
        RecentList.ItemsSource = recentUrls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        UrlBox.Focus();
    }

    private void RecentList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RecentList.SelectedItem is string url)
        {
            UrlBox.Text = url;
            UrlBox.CaretIndex = url.Length;
        }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // 换了链接就清空名称，让主窗口按新链接猜名
        NameBox.Text = "";
    }

    private void Play_Click(object sender, RoutedEventArgs e) => Choose(PlayUrlChoice.Main);

    private void Pip_Click(object sender, RoutedEventArgs e) => Choose(PlayUrlChoice.Pip);

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        Choice = PlayUrlChoice.ImportVod;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Choose(PlayUrlChoice choice)
    {
        if (string.IsNullOrWhiteSpace(PlayUrl))
        {
            MessageBox.Show(this, "请先填写视频链接", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Choice = choice;
        DialogResult = true;
    }
}
