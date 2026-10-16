using System.Windows;
using System.Windows.Controls;

namespace TiviMateWin.Dialogs;

/// <summary>分组管理对话框：重命名 / 新建 / 删除分组</summary>
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

    private void New_Click(object sender, RoutedEventArgs e)
    {
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0) return;
        Action = "rename";
        SelectedGroup = null;
        NewName = name;
        DialogResult = true;
    }

    private void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (GroupList.SelectedItem is not string g) return;
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0 || name == g) return;
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
