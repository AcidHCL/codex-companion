using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopPet;

internal sealed class SharedMemoryWindow : Window
{
    private readonly IChatMemoryStore _store;
    private readonly string _endpoint;
    private readonly Action<bool> _setEnabled;
    private readonly ListBox _list;
    private readonly TextBox _search, _title, _content;
    private readonly CheckBox _pinned, _enabled;
    private readonly TextBlock _status, _confirmText;
    private readonly Border _confirmBox;
    private readonly Button _delete;
    private string? _editingId, _deleteId;
    private bool _sync, _lastEnabled;

    internal SharedMemoryWindow(IChatMemoryStore store, string endpoint, bool enabled, Action<bool> setEnabled, string? initialContent = null)
    {
        _store = store; _endpoint = endpoint; _setEnabled = setEnabled; _lastEnabled = enabled;
        Title = AppVersion.WindowTitle("共享记忆"); Width = 485; Height = 730; MinWidth = 340; MinHeight = 460;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; Resources.MergedDictionaries.Add(Ui.CreateResources());
        var body = new StackPanel { Margin = new Thickness(19, 15, 19, 15) };
        var heading = new DockPanel();
        var close = Ui.Button("×", (_, _) => Close()); close.Name = "SharedMemoryClose"; close.Width = 30; close.Height = 30; close.Padding = new Thickness(0); AutomationProperties.SetName(close, "关闭共享记忆"); DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        var label = Ui.Text("共享记忆", 20, "#214332", true); label.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(label); body.Children.Add(heading);
        heading.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1 && e.OriginalSource is TextBlock) DragMove(); };
        var note = Ui.Text("记忆保存在此电脑，供当前 Ollama 服务器下的所有角色和对话使用。每轮只挑选相关或常用记忆，发送给所选模型。更换服务器后使用独立的记忆库。", 11, "#75866B"); note.TextWrapping = TextWrapping.Wrap; note.LineHeight = 18; note.Margin = new Thickness(0, 8, 0, 5); body.Children.Add(note);
        _enabled = new CheckBox { Name = "SharedMemoryEnabled", Content = "允许聊天使用共享记忆", IsChecked = enabled }; AutomationProperties.SetName(_enabled, "允许聊天使用共享记忆"); body.Children.Add(_enabled);
        _search = ChatLibraryWindow.Input("SharedMemorySearch", ""); _search.ToolTip = "搜索记忆标题或内容"; AutomationProperties.SetName(_search, "搜索记忆标题或内容");
        var searchFrame = ChatLibraryWindow.Frame(_search); searchFrame.Margin = new Thickness(0, 5, 0, 8); body.Children.Add(searchFrame);
        _list = new ListBox { Name = "SharedMemoryList", Height = 156, Background = Brushes.White, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4), ItemContainerStyle = ChatLibraryWindow.SelectionStyle() };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled); AutomationProperties.SetName(_list, "已保存的共享记忆"); body.Children.Add(ChatLibraryWindow.Frame(_list));
        var editorHeading = new DockPanel { Margin = new Thickness(0, 12, 0, 7) };
        var add = Ui.Button("＋ 新增", (_, _) => NewMemory()); add.Name = "SharedMemoryNew"; add.Padding = new Thickness(11, 5, 11, 5); DockPanel.SetDock(add, Dock.Right); editorHeading.Children.Add(add);
        var editorLabel = Ui.Text("记忆内容", 13, "#42643C", true); editorLabel.VerticalAlignment = VerticalAlignment.Center; editorHeading.Children.Add(editorLabel); body.Children.Add(editorHeading);
        _title = ChatLibraryWindow.Input("SharedMemoryTitle", ""); _title.MaxLength = 80; _title.ToolTip = "标题，例如：饮食偏好"; AutomationProperties.SetName(_title, "记忆标题"); body.Children.Add(ChatLibraryWindow.Frame(_title));
        _content = ChatLibraryWindow.Input("SharedMemoryContent", ""); _content.MaxLength = 1000; _content.AcceptsReturn = true; _content.TextWrapping = TextWrapping.Wrap; _content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _content.Height = 110; AutomationProperties.SetName(_content, "记忆内容，最多一千字");
        var contentFrame = ChatLibraryWindow.Frame(_content); contentFrame.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(contentFrame);
        _pinned = new CheckBox { Name = "SharedMemoryPinned", Content = "常用记忆（优先提供给模型）" }; AutomationProperties.SetName(_pinned, "将此条设为常用记忆"); body.Children.Add(_pinned);
        var hint = Ui.Text("只保存你主动添加的内容。也可以在聊天中输入“记住：我喜欢简短回答”。不会从网页或模型回答中自动提取记忆。", 10, "#819175"); hint.TextWrapping = TextWrapping.Wrap; hint.LineHeight = 16; body.Children.Add(hint);
        _confirmText = Ui.Text("", 11, "#874F3C"); _confirmText.TextWrapping = TextWrapping.Wrap;
        var confirmStack = new StackPanel(); confirmStack.Children.Add(_confirmText);
        var confirmActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var confirm = Ui.Button("确认删除", (_, _) => ConfirmDelete()); confirm.Name = "SharedMemoryConfirmDelete"; confirm.Foreground = Ui.Brush("#A34D3A"); confirmActions.Children.Add(confirm);
        var cancel = Ui.Button("取消", (_, _) => CancelDelete()); cancel.Name = "SharedMemoryCancelDelete"; cancel.Margin = new Thickness(7, 0, 0, 0); confirmActions.Children.Add(cancel); confirmStack.Children.Add(confirmActions);
        _confirmBox = new Border { Child = confirmStack, Background = Ui.Brush("#FFF0E7"), Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 9, 0, 0), Visibility = Visibility.Collapsed }; body.Children.Add(_confirmBox);
        _status = Ui.Text("", 11, "#6C8261"); _status.Name = "SharedMemoryStatus"; _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 10, 0, 7); body.Children.Add(_status);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        _delete = Ui.Button("删除记忆", (_, _) => RequestDelete()); _delete.Name = "SharedMemoryDelete"; _delete.Margin = new Thickness(0, 0, 5, 0); actions.Children.Add(_delete);
        var save = Ui.Button("保存记忆", (_, _) => SaveMemory()); save.Name = "SharedMemorySave"; save.Background = Ui.Brush("#356446"); save.Foreground = Brushes.White; save.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(save, 1); actions.Children.Add(save); body.Children.Add(actions);
        var version = Ui.VersionLabel(false); version.HorizontalAlignment = HorizontalAlignment.Right; version.Margin = new Thickness(0, 9, 0, 0); body.Children.Add(version);
        Content = new Border { Background = Ui.Brush("#F7F9F2"), BorderBrush = Ui.Brush("#C9D6C0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        _search.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) => LoadSelected();
        _enabled.Checked += (_, _) => ChangeEnabled(); _enabled.Unchecked += (_, _) => ChangeEnabled();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (_confirmBox.Visibility == Visibility.Visible) CancelDelete(); else Close(); } };
        Loaded += (_, _) => ChatLibraryWindow.FitWindow(this);
        RefreshList(); NewMemory(initialContent);
    }

    private void ChangeEnabled()
    {
        if (_sync) return;
        bool enabled = _enabled.IsChecked == true;
        try { _setEnabled(enabled); _lastEnabled = enabled; SetStatus(enabled ? "已开启：后续消息可以引用相关共享记忆。" : "已暂停使用记忆，已保存的内容仍然保留。"); }
        catch (Exception) { _sync = true; _enabled.IsChecked = _lastEnabled; _sync = false; SetStatus("设置保存失败，保留之前的选项。", true); }
    }
    private void RefreshList(string? selectedId = null)
    {
        try
        {
            selectedId ??= _editingId;
            string query = _search.Text.Trim();
            var all = _store.ListMemories(_endpoint);
            var matches = all.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || item.Content.Contains(query, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.Pinned).ThenByDescending(item => item.UpdatedUtc).ToList();
            _sync = true; _list.Items.Clear();
            foreach (var memory in matches.Take(200))
            {
                var row = new StackPanel { Margin = new Thickness(7, 5, 7, 5) };
                var title = Ui.Text((memory.Pinned ? "★ " : "") + memory.Title, 12, "#294739", memory.Pinned); title.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(title);
                var preview = Ui.Text(memory.Content.Replace('\r', ' ').Replace('\n', ' '), 10, "#85917D"); preview.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(preview);
                var item = new ListBoxItem { Tag = memory, Content = row, ToolTip = memory.Title + " · " + memory.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") };
                _list.Items.Add(item); if (memory.Id == selectedId) _list.SelectedItem = item;
            }
            _sync = false;
            SetStatus(matches.Count == 0 ? "没有匹配的记忆，可以在下方新增。" : $"共 {all.Count} 条记忆" + (matches.Count > 200 ? "，显示前 200 条；可以搜索查找。" : "。选择一条可以编辑。"));
        }
        catch (Exception) { _sync = false; SetStatus("读取记忆失败，请检查本地存储是否可用。", true); }
    }
    private void LoadSelected()
    {
        if (_sync || (_list.SelectedItem as ListBoxItem)?.Tag is not ChatSharedMemory item) return;
        _editingId = item.Id; _title.Text = item.Title; _content.Text = item.Content; _pinned.IsChecked = item.Pinned; _delete.IsEnabled = true; CancelDelete();
        SetStatus("正在编辑：“" + item.Title + "”。修改后点击保存。");
    }
    private void NewMemory(string? content = null)
    {
        _sync = true; _list.SelectedItem = null; _sync = false; _editingId = null; _title.Text = ""; _content.Text = content ?? ""; _pinned.IsChecked = false; _delete.IsEnabled = false; CancelDelete();
        SetStatus(content == null ? "新增记忆：填写标题和内容后保存。" : "已填入待保存内容，请检查后点击“保存记忆”。");
    }
    private void SaveMemory()
    {
        string content = _content.Text.Trim(); string title = _title.Text.Trim();
        if (content.Length == 0) { SetStatus("请先填写需要记住的内容。", true); return; }
        if (SharedMemoryContext.ContainsLikelySecret(content)) { SetStatus("这段内容似乎包含密码或密钥，请去掉具体凭据后保存。", true); return; }
        if (title.Length == 0) title = content.Replace('\r', ' ').Replace('\n', ' '); if (title.Length > 40 && _title.Text.Trim().Length == 0) title = title[..40] + "…";
        try
        {
            ChatSharedMemory saved = _store.UpsertMemory(new ChatSharedMemory(_editingId ?? "", _endpoint, title, content, _pinned.IsChecked == true, DateTimeOffset.UtcNow));
            _editingId = saved.Id; _title.Text = saved.Title; _delete.IsEnabled = true; CancelDelete(); RefreshList(saved.Id); SetStatus("已保存。后续对话将按相关性和常用标记选取这条记忆。");
        }
        catch (ArgumentException) { SetStatus("请检查标题和内容的长度：标题最多 80 字，内容最多 1000 字。", true); }
        catch (Exception) { SetStatus("记忆保存失败，你的编辑仍在此窗口中。", true); }
    }
    private void RequestDelete()
    {
        if (_editingId == null) return;
        _deleteId = _editingId; _confirmText.Text = "删除这条共享记忆？之后所有对话都不再检索它；已有聊天文字会保留。"; _confirmBox.Visibility = Visibility.Visible;
    }
    private void CancelDelete() { _deleteId = null; _confirmBox.Visibility = Visibility.Collapsed; }
    private void ConfirmDelete()
    {
        if (_deleteId == null) return;
        try { _store.DeleteMemory(_deleteId); CancelDelete(); NewMemory(); RefreshList(); SetStatus("已删除共享记忆。已有对话中的文字没有改动。"); }
        catch (Exception) { SetStatus("删除失败，请重试。", true); }
    }
    private void SetStatus(string message, bool error = false) { _status.Text = message; _status.Foreground = Ui.Brush(error ? "#A44F39" : "#6C8261"); }
}
