using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TiviMateWin.Services;

namespace TiviMateWin.Dialogs;

/// <summary>播放列表管理：上半添加新源，下半列出已有源（改名 / 删除 / 设为当前）</summary>
public partial class AddPlaylistWindow : Window
{
    private readonly MainWindow _owner;
    public string SourceName => NameBox.Text.Trim();
    public string SourceUrl => UrlBox.Text.Trim();

    public AddPlaylistWindow(MainWindow owner)
    {
        InitializeComponent();
        _owner = owner;
        RebuildRows();
    }

    private void RebuildRows()
    {
        ListPanel.Children.Clear();
        var active = _owner.ActivePlaylistName;
        CountText.Text = $"已有播放列表（{_owner.Playlists.Count} 个，名称可直接编辑，● 为当前列表）";
        foreach (var s in _owner.Playlists)
            ListPanel.Children.Add(BuildRow(s, s.Name == active));
    }

    private UIElement BuildRow(PlaylistSource s, bool isCurrent)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 当前列表标记
        var mark = new TextBlock
        {
            Text = "●",
            Foreground = System.Windows.Media.Brushes.DodgerBlue,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = isCurrent ? Visibility.Visible : Visibility.Hidden
        };
        Grid.SetColumn(mark, 0);
        grid.Children.Add(mark);

        // 名称（可直接编辑，LostFocus 保存；重名自动还原）
        var nameBox = new TextBox
        {
            Text = s.Name,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderBrush = null,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(2, 3, 2, 3)
        };
        nameBox.LostFocus += (_, _) =>
        {
            if (nameBox.Text.Trim() != s.Name)
            {
                _owner.RenamePlaylist(s, nameBox.Text);
                RebuildRows();
            }
        };
        Grid.SetColumn(nameBox, 1);
        grid.Children.Add(nameBox);

        // 地址（只读显示，ToolTip 看全）
        var url = new TextBlock
        {
            Text = s.Url,
            Foreground = System.Windows.Media.Brushes.Gray,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            ToolTip = s.Url
        };
        Grid.SetColumn(url, 2);
        url.Margin = new Thickness(10, 0, 10, 0);
        grid.Children.Add(url);

        // 操作按钮
        var ops = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (!isCurrent)
        {
            var btnUse = MakeBtn("设为当前", "#FF1F4E7A");
            btnUse.Click += (_, _) => { _owner.ActivatePlaylist(s.Name); RebuildRows(); };
            ops.Children.Add(btnUse);
        }

        var btnDel = MakeBtn("删除", "#FF7A2E2E");
        btnDel.Margin = new Thickness(6, 0, 0, 0);
        btnDel.Click += (_, _) =>
        {
            if (MessageBox.Show(this, $"删除播放列表“{s.Name}”？", "确认",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            _owner.RemovePlaylist(s);
            RebuildRows();
        };
        ops.Children.Add(btnDel);

        Grid.SetColumn(ops, 3);
        grid.Children.Add(ops);
        return grid;
    }

    private static Button MakeBtn(string text, string bg)
    {
        var b = new Button
        {
            Content = text,
            Background = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(bg)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 3, 8, 3),
            FontSize = 12,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        return b;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 M3U 播放列表文件",
            Filter = "M3U 播放列表|*.m3u;*.m3u8|所有文件|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            UrlBox.Text = dlg.FileName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(UrlBox.Text))
        {
            MessageBox.Show(this, "请填写播放列表地址或选择本地文件", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        DialogResult = true;
    }
}
