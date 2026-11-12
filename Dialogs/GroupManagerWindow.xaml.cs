using System.Windows;
using System.Windows.Controls;

namespace TiviMateWin.Dialogs;

/// <summary>分组管理对话框：重命名 / 删除分组</summary>
public partial class GroupManagerWindow : Window
{
    private readonly List<string> _groups;

    public string? Action { get; private set; }
    public string? SelectedGroup { get; private set; }
    public string NewName { get; private set; } = "";

    public GroupManagerWindow(List<string> groups)
    {
        InitializeComponent();
        _groups = groups;
        foreach (var g in groups) GroupList.Items.Add(g);
    }

    private void GroupList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (GroupList.SelectedItem is string g) NewNameBox.Text = g;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not string g) return;
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0 || name == g) return;
        // 深查第 23 轮：改成已存在的分组名会把两组静默合并（与播放列表改名
        // “重名拒绝”口径不一致）；此处直接拦截并提示，由用户换名。
        if (_groups.Any(x => x != g && x == name))
        {
            MessageBox.Show(this, $"分组「{name}」已存在，请换一个名称。", "重名",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Action = "rename";
        SelectedGroup = g;
        NewName = name;
        DialogResult = true;
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not string g) return;
        if (MessageBox.Show(this, $"删除分组「{g}」？频道将移到未分组。", "删除分组",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Action = "delete";
        SelectedGroup = g;
        DialogResult = true;
    }
}
