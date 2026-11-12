using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using TiviMateWin.Models;
using TiviMateWin.Services;

namespace TiviMateWin.Dialogs;

/// <summary>点播源浏览：导入 vod.json → 选站点 → 看最新/搜片 → 播放（主窗口/小窗）。
/// 仅支持 type=1 的 JSON 站点；XML 站点只列出、不加载。</summary>
public partial class VodBrowserWindow : Window
{
    private sealed class SiteRow
    {
        public required VodSource.Site Site { get; init; }
        public string Display => Site.Supported ? Site.Name : Site.Name + "（XML，不支持）";
        public Brush Brush => Site.Supported ? Brushes.White : Brushes.Gray;
    }

    private sealed class VideoRow
    {
        public required VodSource.Video Video { get; init; }
        public string Name => Video.Name;
        public string Remarks => Video.Remarks ?? "";
    }

    private readonly MainWindow _owner;
    private readonly HttpClient _http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
    })
    { Timeout = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? _cts;
    private VodSource.Site? _site;
    private int _page = 1;
    private int _pageCount = 1;
    private string _keyword = "";
    private bool _busy;

    public VodBrowserWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        SourceBox.Focus();
    }

    private void Window_Closed(object sender, EventArgs e)
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _http.Dispose();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 vod.json 点播源文件",
            Filter = "点播源|*.json|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true) SourceBox.Text = dlg.FileName;
    }

    private async void Load_Click(object sender, RoutedEventArgs e)
    {
        var src = SourceBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(src))
        {
            StatusText.Text = "请先填写 vod.json 地址或选择本地文件。";
            return;
        }
        SetBusy(true, "正在加载点播源…");
        try
        {
            string json;
            if (src.Contains("://"))
            {
                using var res = await _http.GetAsync(src, _cts!.Token);
                res.EnsureSuccessStatusCode();
                json = await res.Content.ReadAsStringAsync(_cts.Token);
            }
            else
            {
                if (!File.Exists(src)) throw new FileNotFoundException("文件不存在", src);
                json = await File.ReadAllTextAsync(src, _cts!.Token);
            }
            var sites = VodSource.ParseSites(json);
            SiteList.ItemsSource = sites.Select(s => new SiteRow { Site = s }).ToList();
            VideoList.ItemsSource = null;
            _site = null;
            SetPlayEnabled(false);
            StatusText.Text = sites.Count == 0
                ? "未解析到可用站点：请确认是 vod.json（带 sites 数组）。"
                : $"已载入 {sites.Count} 个站点（{sites.Count(s => s.Supported)} 个可用），点站点浏览影片。";
        }
        catch (OperationCanceledException) { /* 新请求/关闭导致取消，不报错 */ }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private async void SiteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SiteList.SelectedItem is not SiteRow row) return;
        if (!row.Site.Supported)
        {
            StatusText.Text = "该站点是 XML 接口（type=0），本版只支持 JSON 接口（type=1）。";
            return;
        }
        _site = row.Site;
        _keyword = "";
        SearchBox.Text = "";
        await LoadPageAsync(1);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (_site is null) { StatusText.Text = "请先选一个站点。"; return; }
        _keyword = SearchBox.Text.Trim();
        await LoadPageAsync(1);
    }

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;   // 不吞掉的话回车还会触发默认按钮（播放）
        Search_Click(sender, e);
    }

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        if (_page > 1) await LoadPageAsync(_page - 1);
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_page < _pageCount) await LoadPageAsync(_page + 1);
    }

    private async Task LoadPageAsync(int page)
    {
        if (_site is null || _busy) return;
        SetBusy(true, "正在加载影片列表…");
        try
        {
            var url = string.IsNullOrEmpty(_keyword)
                ? VodSource.BuildListUrl(_site.Api, page)
                : VodSource.BuildSearchUrl(_site.Api, _keyword, page);
            using var res = await _http.GetAsync(url, _cts!.Token);
            res.EnsureSuccessStatusCode();
            var json = await res.Content.ReadAsStringAsync(_cts.Token);
            var (videos, pageCount) = VodSource.ParseVideoList(json);
            _page = page;
            _pageCount = pageCount;
            VideoList.ItemsSource = videos.Select(v => new VideoRow { Video = v }).ToList();
            PageText.Text = $"第 {_page} / {_pageCount} 页";
            PrevBtn.IsEnabled = _page > 1;
            NextBtn.IsEnabled = _page < _pageCount;
            SetPlayEnabled(videos.Count > 0);
            StatusText.Text = videos.Count == 0
                ? "本页没有影片。" : $"已载入 {videos.Count} 部影片，双击播放。";
        }
        catch (OperationCanceledException) { /* 取消不报错 */ }
        catch (Exception ex)
        {
            StatusText.Text = "加载失败: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void VideoList_DoubleClick(object sender, MouseButtonEventArgs e) => _ = PlaySelectedAsync(false);

    private void Play_Click(object sender, RoutedEventArgs e) => _ = PlaySelectedAsync(false);

    private void Pip_Click(object sender, RoutedEventArgs e) => _ = PlaySelectedAsync(true);

    private async Task PlaySelectedAsync(bool pip)
    {
        if (VideoList.SelectedItem is not VideoRow row) return;
        if (_site is null) return;
        SetBusy(true, "正在取播放地址…");
        try
        {
            // 列表里已有播放地址就直接用；没有则拉详情取
            var playUrl = VodSource.ExtractPlayUrl(row.Video.PlayUrl);
            if (playUrl is null)
            {
                var url = VodSource.BuildDetailUrl(_site.Api, row.Video.Id);
                using var res = await _http.GetAsync(url, _cts!.Token);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadAsStringAsync(_cts.Token);
                var (videos, _) = VodSource.ParseVideoList(json);
                playUrl = videos.Count > 0 ? VodSource.ExtractPlayUrl(videos[0].PlayUrl) : null;
            }
            if (playUrl is null)
            {
                StatusText.Text = $"“{row.Video.Name}”没有可播地址。";
                return;
            }
            _owner.PlayVodItem(new ChannelItem
            {
                Name = row.Video.Name,
                Url = playUrl,
                Group = "点播",
                Logo = row.Video.Pic,
            }, pip);
            StatusText.Text = $"正在播放“{row.Video.Name}”。";
            if (!pip) Close();   // 主窗口播放后关闭浏览窗；小窗播放则留着继续选片
        }
        catch (OperationCanceledException) { /* 取消不报错 */ }
        catch (Exception ex)
        {
            StatusText.Text = "取播放地址失败: " + ex.Message;
        }
        finally { SetBusy(false); }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetPlayEnabled(bool enabled)
    {
        PlayBtn.IsEnabled = enabled;
        PipBtn.IsEnabled = enabled;
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        if (busy)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            if (status != null) StatusText.Text = status;
        }
    }
}
