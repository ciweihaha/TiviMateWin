using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
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

    /// <summary>一条可选线路/剧集：显示“线路 · 集名（可用/失效/未测）”</summary>
    private sealed class CandidateRow
    {
        public required VodSource.PlayCandidate Candidate { get; init; }
        public required string Status { get; init; }   // 可用 / 失效 / 未测
        public string Display => $"{Candidate.Source} · {Candidate.Episode}（{Status}）";
    }

    private enum ProbeResult { Ok, Dead, Unknown }

    /// <summary>影视 App 点播聚合地址（与电视影视 App 同一份 vod.json），打开即预填并自动加载。</summary>
    private const string DefaultVodJson = "https://live.ciweihaha.win/vod.json";

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
    private List<VodSource.PlayCandidate> _candidates = new();

    public VodBrowserWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        if (string.IsNullOrWhiteSpace(SourceBox.Text)) SourceBox.Text = DefaultVodJson;
        Loaded += VodBrowserWindow_Loaded;
        SourceBox.Focus();
    }

    private void VodBrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 默认地址打开即自动加载，用户改填其他地址时不自动加载
        if (SourceBox.Text.Trim() == DefaultVodJson) Load_Click(this, new RoutedEventArgs());
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

    private void VideoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoList.SelectedItem is VideoRow row) _ = LoadCandidatesAsync(row.Video);
    }

    /// <summary>
    /// 取选中影片的全部线路×剧集，并按线路探活（每条线路只测第一集，6 秒超时）：
    /// 自动选中首条可用线路的第一集；无可用线路时选中首条“未测”，失效线路仍可手动选。
    /// </summary>
    private async Task LoadCandidatesAsync(VodSource.Video video)
    {
        _candidates = new List<VodSource.PlayCandidate>();
        CandidateBox.ItemsSource = null;
        CandidateBox.IsEnabled = false;
        CandidateHint.Text = "正在取线路…";
        try
        {
            _cts ??= new CancellationTokenSource();
            var playUrl = video.PlayUrl;
            var playFrom = video.PlayFrom;
            if (string.IsNullOrWhiteSpace(playUrl))
            {
                if (_site is null) return;
                var url = VodSource.BuildDetailUrl(_site.Api, video.Id);
                using var res = await _http.GetAsync(url, _cts.Token);
                res.EnsureSuccessStatusCode();
                var json = await res.Content.ReadAsStringAsync(_cts.Token);
                var (videos, _) = VodSource.ParseVideoList(json);
                if (videos.Count == 0) { CandidateHint.Text = "没有可播地址。"; return; }
                playUrl = videos[0].PlayUrl;
                playFrom = videos[0].PlayFrom;
            }
            var all = VodSource.ExtractAllPlayUrls(playUrl, playFrom);
            if (all.Count == 0) { CandidateHint.Text = "没有可播地址。"; return; }
            _candidates = all;
            var sources = all.GroupBy(c => c.Source).Select(g => g.First()).ToList();
            var probeResults = await Task.WhenAll(sources.Select(c => ProbeUrlAsync(c.Url)));
            var alive = new HashSet<string>();
            for (var i = 0; i < sources.Count; i++)
                if (probeResults[i] == ProbeResult.Ok) alive.Add(sources[i].Source);
            var dead = new HashSet<string>();
            for (var i = 0; i < sources.Count; i++)
                if (probeResults[i] == ProbeResult.Dead) dead.Add(sources[i].Source);
            var rows = all.Select(c => new CandidateRow
            {
                Candidate = c,
                Status = alive.Contains(c.Source) ? "可用" : dead.Contains(c.Source) ? "失效" : "未测",
            }).ToList();
            CandidateBox.ItemsSource = rows;
            CandidateBox.IsEnabled = true;
            var selected = rows.FirstOrDefault(r => r.Status == "可用")
                ?? rows.FirstOrDefault(r => r.Status != "失效")
                ?? rows[0];
            CandidateBox.SelectedItem = selected;
            CandidateHint.Text = $"共 {sources.Count} 条线路，{alive.Count} 条可用；当前：{selected.Candidate.Source} · {selected.Candidate.Episode}";
        }
        catch (OperationCanceledException) { /* 取消不报错 */ }
        catch (Exception ex)
        {
            CandidateHint.Text = "取线路失败: " + ex.Message;
        }
    }

    /// <summary>探一条 m3u8 是否能播：200 且内容为 #EXTM3U 记“可用”；404/403 记“失效”；超时或其他异常记“未测”（仍可手动试播）。</summary>
    private async Task<ProbeResult> ProbeUrlAsync(string url)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (res.StatusCode == HttpStatusCode.NotFound || res.StatusCode == HttpStatusCode.Forbidden)
                return ProbeResult.Dead;
            if (!res.IsSuccessStatusCode) return ProbeResult.Unknown;
            var media = res.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("mpegurl") || media.Contains("mp2t")) return ProbeResult.Ok;
            using var stream = await res.Content.ReadAsStreamAsync(cts.Token);
            var buf = new byte[512];
            var n = await stream.ReadAsync(buf, 0, buf.Length, cts.Token);
            var head = System.Text.Encoding.UTF8.GetString(buf, 0, n);
            return head.Contains("#EXTM3U") ? ProbeResult.Ok : ProbeResult.Unknown;
        }
        catch (OperationCanceledException) { return ProbeResult.Unknown; }
        catch (HttpRequestException) { return ProbeResult.Unknown; }
        catch (Exception) { return ProbeResult.Unknown; }
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
            // 优先用线路/选集里选中的候选（已探活，首选可用线路）；没有则先取候选再播
            string? playUrl = (CandidateBox.SelectedItem as CandidateRow)?.Candidate.Url;
            string? playName = (CandidateBox.SelectedItem as CandidateRow) is { } crow
                ? $"{row.Video.Name}（{crow.Candidate.Source} · {crow.Candidate.Episode}）"
                : row.Video.Name;
            if (playUrl is null)
            {
                await LoadCandidatesAsync(row.Video);
                if (CandidateBox.SelectedItem is CandidateRow picked)
                {
                    playUrl = picked.Candidate.Url;
                    playName = $"{row.Video.Name}（{picked.Candidate.Source} · {picked.Candidate.Episode}）";
                }
            }
            if (playUrl is null)
            {
                // 兜底：旧逻辑——列表自带地址直接用，否则拉详情取首条
                playUrl = VodSource.ExtractPlayUrl(row.Video.PlayUrl);
                if (playUrl is null)
                {
                    var url = VodSource.BuildDetailUrl(_site.Api, row.Video.Id);
                    using var res = await _http.GetAsync(url, _cts!.Token);
                    res.EnsureSuccessStatusCode();
                    var json = await res.Content.ReadAsStringAsync(_cts.Token);
                    var (videos, _) = VodSource.ParseVideoList(json);
                    playUrl = videos.Count > 0 ? VodSource.ExtractPlayUrl(videos[0].PlayUrl) : null;
                }
            }
            if (playUrl is null)
            {
                StatusText.Text = $"“{row.Video.Name}”没有可播地址。";
                return;
            }
            _owner.PlayVodItem(new ChannelItem
            {
                Name = playName,
                Url = playUrl,
                Group = "点播",
                Logo = row.Video.Pic,
            }, pip);
            StatusText.Text = $"正在播放“{playName}”。";
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
