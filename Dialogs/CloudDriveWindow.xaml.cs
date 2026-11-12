using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TiviMateWin.Models;
using TiviMateWin.Services;

namespace TiviMateWin.Dialogs;

/// <summary>网盘浏览：经 OpenList 列目录，视频文件取直链播放（主窗口/小窗）。
/// 只显示文件夹与视频文件；直链自带签名，直接交给播放器。</summary>
public partial class CloudDriveWindow : Window
{
    private sealed class FileRow
    {
        public required OpenListService.Entry Entry { get; init; }
        public string Name => Entry.Name;
        public bool IsDir => Entry.IsDir;
        public bool IsVideo => !Entry.IsDir && VodSource.IsVideoFile(Entry.Name);
        public string Display => Entry.IsDir ? "📁 " + Entry.Name : "🎬 " + Entry.Name;
        public string SizeText => Entry.IsDir ? "" : FormatSize(Entry.Size);

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024L * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("0.0") + " MB";
            return (bytes / 1024.0 / 1024 / 1024).ToString("0.00") + " GB";
        }
    }

    private readonly MainWindow _owner;
    private readonly OpenListService _service;
    private string _path = "/";
    private bool _busy;

    public CloudDriveWindow(MainWindow owner, AppConfig config)
    {
        InitializeComponent();
        _owner = owner;
        _service = new OpenListService(
            config.OpenListServer ?? "", config.OpenListUsername ?? "", config.OpenListPassword ?? "");
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await LoadPathAsync("/");

    private void Window_Closed(object sender, EventArgs e) => _service.Dispose();

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        if (_path != "/") _ = LoadPathAsync(OpenListService.ParentPath(_path));
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = LoadPathAsync(_path);

    private async Task LoadPathAsync(string path)
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = "正在加载…";
        SetPlayEnabled(false);
        try
        {
            var entries = await _service.ListAsync(path);
            if (entries.Count == 0)
            {
                // 空列表可能是空目录，也可能是登录失败；登录失败时给明确提示
                StatusText.Text = "该目录为空，或 OpenList 登录失败（请检查设置里的服务器地址与账号）。";
            }
            else
            {
                StatusText.Text = $"共 {entries.Count} 项（只显示文件夹与视频文件）。";
            }
            _path = path;
            PathText.Text = path;
            FileList.ItemsSource = entries
                .Where(e => e.IsDir || VodSource.IsVideoFile(e.Name))
                .OrderByDescending(e => e.IsDir)
                .ThenBy(e => e.Name, StringComparer.CurrentCulture)
                .Select(e => new FileRow { Entry = e })
                .ToList();
        }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败: " + ex.Message;
        }
        finally { _busy = false; }
    }

    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        SetPlayEnabled(FileList.SelectedItem is FileRow row && row.IsVideo);
    }

    private void FileList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is not FileRow row) return;
        if (row.IsDir) _ = LoadPathAsync(row.Entry.Path);
        else if (row.IsVideo) _ = PlaySelectedAsync(false);
    }

    private void Play_Click(object sender, RoutedEventArgs e) => _ = PlaySelectedAsync(false);

    private void Pip_Click(object sender, RoutedEventArgs e) => _ = PlaySelectedAsync(true);

    private async Task PlaySelectedAsync(bool pip)
    {
        if (FileList.SelectedItem is not FileRow row || !row.IsVideo) return;
        if (_busy) return;
        _busy = true;
        StatusText.Text = "正在取播放直链…";
        try
        {
            var url = await _service.GetRawUrlAsync(row.Entry.Path);
            if (string.IsNullOrWhiteSpace(url))
            {
                StatusText.Text = $"“{row.Name}”取直链失败，请检查 OpenList 登录与该文件权限。";
                return;
            }
            _owner.PlayVodItem(new ChannelItem
            {
                Name = Path.GetFileNameWithoutExtension(row.Name),
                Url = url,
                Group = "网盘",
            }, pip);
            StatusText.Text = $"正在播放“{row.Name}”。";
            if (!pip) Close();   // 主窗口播放后关闭浏览窗；小窗播放则留着继续选片
        }
        catch (Exception ex)
        {
            StatusText.Text = "取直链失败: " + ex.Message;
        }
        finally { _busy = false; }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetPlayEnabled(bool enabled)
    {
        PlayBtn.IsEnabled = enabled;
        PipBtn.IsEnabled = enabled;
    }
}
