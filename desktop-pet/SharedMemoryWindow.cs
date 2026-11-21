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
    private readonly Action<bool, bool> _savePreferences;
    private readonly ListBox _list;
    private readonly TextBox _search, _title, _content;
    private readonly CheckBox _pinned, _enabled, _automatic;
    private readonly TextBlock _status, _confirmText;
    private readonly Border _confirmBox;
    private readonly Button _delete;
    private string? _editingId, _deleteId;
    private bool _sync, _lastEnabled, _lastAutomatic;

    internal SharedMemoryWindow(IChatMemoryStore store, string endpoint, bool enabled, bool automatic,
        Action<bool, bool> savePreferences, string? initialContent = null)
    {
        _store = store; _endpoint = endpoint; _savePreferences = savePreferences; _lastEnabled = enabled; _lastAutomatic = automatic;
        Title = AppVersion.WindowTitle(L.T("共享记忆", "Shared memory")); Width = L.IsEnglish ? 550 : 485; Height = L.IsEnglish ? 780 : 730; MinWidth = 340; MinHeight = 460;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; Resources.MergedDictionaries.Add(Ui.CreateResources());
        var body = new StackPanel { Margin = new Thickness(19, 15, 19, 15) };
        var heading = new DockPanel();
        var close = Ui.Button("×", (_, _) => Close()); close.Name = "SharedMemoryClose"; close.Width = 30; close.Height = 30; close.Padding = new Thickness(0); AutomationProperties.SetName(close, L.T("关闭共享记忆", "Close shared memory")); DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        var label = Ui.Text(L.T("共享记忆", "Shared memory"), 20, "#214332", true); label.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(label); body.Children.Add(heading);
        heading.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1 && e.OriginalSource is TextBlock) DragMove(); };
        var note = Ui.Text(L.T("记忆保存在本机，同一模型服务下的角色和对话可引用相关或常用条目。", "Memories are stored on this computer. Chats and characters on the same model service can use relevant or pinned entries."), 11, "#65735D"); note.TextWrapping = TextWrapping.Wrap; note.LineHeight = 18; note.Margin = new Thickness(0, 8, 0, 5); body.Children.Add(note);
        _enabled = new CheckBox { Name = "SharedMemoryEnabled", Content = L.T("启用共享记忆", "Use shared memory"), IsChecked = enabled }; body.Children.Add(_enabled);
        string automaticLabel = L.T("自动记住值得保留的事", "Automatically save memorable moments");
        _automatic = new CheckBox { Name = "SharedMemoryAutomatic", Content = new TextBlock { Text = automaticLabel, TextWrapping = TextWrapping.Wrap }, IsChecked = automatic, IsEnabled = enabled, Opacity = enabled ? 1 : .55, Margin = new Thickness(16, 4, 0, 0) };
        AutomationProperties.SetName(_automatic, automaticLabel); body.Children.Add(_automatic);
        var automaticNote = Ui.Text(L.T("开启后，当前模型会从已完成的对话、已展示的陪伴发言及其事件概述中挑选记忆。使用远程服务时，这些文字将发送给该服务，API 调用可能产生费用。", "The selected model reviews text from completed chats and displayed companion remarks with their event summaries. Remote services receive this text. API charges may apply."), 11, "#65735D");
        automaticNote.Margin = new Thickness(16, 3, 0, 5); automaticNote.TextWrapping = TextWrapping.Wrap; body.Children.Add(automaticNote);
        _search = ChatLibraryWindow.Input("SharedMemorySearch", ""); _search.ToolTip = L.T("搜索记忆标题或内容", "Search memory titles or contents"); AutomationProperties.SetName(_search, L.T("搜索记忆标题或内容", "Search memory titles or contents"));
        var searchFrame = ChatLibraryWindow.Frame(_search); searchFrame.Margin = new Thickness(0, 5, 0, 8); body.Children.Add(searchFrame);
        _list = new ListBox { Name = "SharedMemoryList", Height = 156, Background = Brushes.White, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4), ItemContainerStyle = ChatLibraryWindow.SelectionStyle() };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled); AutomationProperties.SetName(_list, L.T("已保存的共享记忆", "Saved shared memories")); body.Children.Add(ChatLibraryWindow.Frame(_list));
        var editorHeading = new DockPanel { Margin = new Thickness(0, 12, 0, 7) };
        var add = Ui.Button(L.T("＋ 新增", "＋ Add"), (_, _) => NewMemory()); add.Name = "SharedMemoryNew"; add.Padding = new Thickness(11, 5, 11, 5); DockPanel.SetDock(add, Dock.Right); editorHeading.Children.Add(add);
        var editorLabel = Ui.Text(L.T("记忆内容", "Memory content"), 13, "#42643C", true); editorLabel.VerticalAlignment = VerticalAlignment.Center; editorHeading.Children.Add(editorLabel); body.Children.Add(editorHeading);
        _title = ChatLibraryWindow.Input("SharedMemoryTitle", ""); _title.MaxLength = 80; _title.ToolTip = L.T("标题，例如：饮食偏好", "Title, e.g. dietary preferences"); AutomationProperties.SetName(_title, L.T("记忆标题", "Memory title")); body.Children.Add(ChatLibraryWindow.Frame(_title));
        _content = ChatLibraryWindow.Input("SharedMemoryContent", ""); _content.MaxLength = 1000; _content.AcceptsReturn = true; _content.TextWrapping = TextWrapping.Wrap; _content.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _content.Height = 110; AutomationProperties.SetName(_content, L.T("记忆内容，最多一千字", "Memory content, up to 1000 characters"));
        var contentFrame = ChatLibraryWindow.Frame(_content); contentFrame.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(contentFrame);
        _pinned = new CheckBox { Name = "SharedMemoryPinned", Content = L.T("常用记忆（优先提供给模型）", "Pin memory (prioritize for the model)") }; AutomationProperties.SetName(_pinned, L.T("将此条设为常用记忆", "Pin this memory")); body.Children.Add(_pinned);
        _confirmText = Ui.Text("", 11, "#874F3C"); _confirmText.TextWrapping = TextWrapping.Wrap;
        var confirmStack = new StackPanel(); confirmStack.Children.Add(_confirmText);
        var confirmActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var confirm = Ui.Button(L.T("确认删除", "Confirm deletion"), (_, _) => ConfirmDelete()); confirm.Name = "SharedMemoryConfirmDelete"; confirm.Foreground = Ui.Brush("#A34D3A"); confirmActions.Children.Add(confirm);
        var cancel = Ui.Button(L.T("取消", "Cancel"), (_, _) => CancelDelete()); cancel.Name = "SharedMemoryCancelDelete"; cancel.Margin = new Thickness(7, 0, 0, 0); confirmActions.Children.Add(cancel); confirmStack.Children.Add(confirmActions);
        _confirmBox = new Border { Child = confirmStack, Background = Ui.Brush("#FFF0E7"), Padding = new Thickness(10), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 9, 0, 0), Visibility = Visibility.Collapsed }; body.Children.Add(_confirmBox);
        _status = Ui.Text("", 11, "#6C8261"); _status.Name = "SharedMemoryStatus"; _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 10, 0, 7); body.Children.Add(_status);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        _delete = Ui.Button(L.T("删除记忆", "Delete memory"), (_, _) => RequestDelete()); _delete.Name = "SharedMemoryDelete"; _delete.Margin = new Thickness(0, 0, 5, 0); actions.Children.Add(_delete);
        var save = Ui.Button(L.T("保存记忆", "Save memory"), (_, _) => SaveMemory()); save.Name = "SharedMemorySave"; save.Background = Ui.Brush("#356446"); save.Foreground = Brushes.White; save.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(save, 1); actions.Children.Add(save); body.Children.Add(actions);
        var version = Ui.VersionLabel(false); version.HorizontalAlignment = HorizontalAlignment.Right; version.Margin = new Thickness(0, 9, 0, 0); body.Children.Add(version);
        Content = new Border { Background = Ui.Brush("#F7F9F2"), BorderBrush = Ui.Brush("#C9D6C0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        _search.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) => LoadSelected();
        _enabled.Checked += (_, _) => SavePreferences(); _enabled.Unchecked += (_, _) => SavePreferences();
        _automatic.Checked += (_, _) => SavePreferences(); _automatic.Unchecked += (_, _) => SavePreferences();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (_confirmBox.Visibility == Visibility.Visible) CancelDelete(); else Close(); } };
        Loaded += (_, _) => ChatLibraryWindow.FitWindow(this);
        RefreshList(); NewMemory(initialContent);
    }

    private void SavePreferences()
    {
        if (_sync) return;
        bool enabled = _enabled.IsChecked == true, automatic = _automatic.IsChecked == true;
        try
        {
            _savePreferences(enabled, automatic);
            _lastEnabled = enabled; _lastAutomatic = automatic;
            SetStatus(enabled ? L.T("已保存设置。", "Settings saved.") : L.T("共享记忆已暂停。仍可手动保存和编辑。", "Shared memory paused. You can still save and edit entries manually."));
        }
        catch (Exception)
        {
            _sync = true; _enabled.IsChecked = _lastEnabled; _automatic.IsChecked = _lastAutomatic; _sync = false;
            SetStatus(L.T("设置保存失败，请重试。", "Could not save settings. Please try again."), true);
        }
        _automatic.IsEnabled = _lastEnabled;
        _automatic.Opacity = _lastEnabled ? 1 : .55;
    }
    internal void RefreshList(string? selectedId = null)
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
                string origin = memory.IsAutomatic ? L.T("自动", "Automatic") : L.T("手动", "Manual");
                var preview = Ui.Text(origin + " · " + memory.Content.Replace('\r', ' ').Replace('\n', ' '), 10, "#65735D"); preview.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(preview);
                var item = new ListBoxItem { Tag = memory, Content = row, ToolTip = memory.Title + " · " + memory.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") };
                AutomationProperties.SetName(item, memory.Title + " · " + origin);
                _list.Items.Add(item); if (memory.Id == selectedId) _list.SelectedItem = item;
            }
            _sync = false;
            SetStatus(matches.Count == 0 ? L.T("没有匹配的记忆，可以在下方新增。", "No matching memories. You can add one below.") : L.T($"共 {all.Count} 条记忆", $"{all.Count} memory entry/entries") + (matches.Count > 200 ? L.T("，显示前 200 条；可以搜索查找。", "; showing the first 200. Search to find others.") : L.T("。选择一条可以编辑。", ". Select an entry to edit.")));
        }
        catch (Exception) { _sync = false; SetStatus(L.T("读取记忆失败，请检查本地存储是否可用。", "Could not read memories. Check that local storage is available."), true); }
    }
    private void LoadSelected()
    {
        if (_sync || (_list.SelectedItem as ListBoxItem)?.Tag is not ChatSharedMemory item) return;
        _editingId = item.Id; _title.Text = item.Title; _content.Text = item.Content; _pinned.IsChecked = item.Pinned; _delete.IsEnabled = true; CancelDelete();
        SetStatus(L.T("正在编辑：“", "Editing “") + item.Title + L.T("”。修改后点击保存。", "”. Click Save when finished."));
    }
    private void NewMemory(string? content = null)
    {
        _sync = true; _list.SelectedItem = null; _sync = false; _editingId = null; _title.Text = ""; _content.Text = content ?? ""; _pinned.IsChecked = false; _delete.IsEnabled = false; CancelDelete();
        SetStatus(content == null ? L.T("新增记忆：填写标题和内容后保存。", "New memory: enter a title and content, then save.") : L.T("已填入待保存内容，请检查后点击“保存记忆”。", "Content filled in. Review it, then click Save memory."));
    }
    private void SaveMemory()
    {
        string content = _content.Text.Trim(); string title = _title.Text.Trim();
        if (content.Length == 0) { SetStatus(L.T("请先填写需要记住的内容。", "Enter the content you want remembered first."), true); return; }
        if (SharedMemoryContext.ContainsLikelySecret(content)) { SetStatus(L.T("这段内容似乎包含密码或密钥，请去掉具体凭据后保存。", "This content may contain a password or key. Remove credentials before saving."), true); return; }
        if (title.Length == 0) title = content.Replace('\r', ' ').Replace('\n', ' '); if (title.Length > 40 && _title.Text.Trim().Length == 0) title = title[..40] + "…";
        try
        {
            ChatSharedMemory saved = _store.UpsertMemory(new ChatSharedMemory(_editingId ?? "", _endpoint, title, content, _pinned.IsChecked == true, DateTimeOffset.UtcNow));
            _editingId = saved.Id; _title.Text = saved.Title; _delete.IsEnabled = true; CancelDelete(); RefreshList(saved.Id); SetStatus(L.T("已保存。后续对话将按相关性和常用标记选取这条记忆。", "Saved. Future conversations select this memory based on relevance and its pinned status."));
        }
        catch (ArgumentException) { SetStatus(L.T("请检查标题和内容的长度：标题最多 80 字，内容最多 1000 字。", "Check the length: titles allow up to 80 characters and content up to 1000."), true); }
        catch (Exception) { SetStatus(L.T("记忆保存失败，你的编辑仍在此窗口中。", "Could not save the memory. Your edits are still in this window."), true); }
    }
    private void RequestDelete()
    {
        if (_editingId == null) return;
        _deleteId = _editingId; _confirmText.Text = L.T("删除这条共享记忆？之后所有对话都不再检索它；已有聊天文字会保留。", "Delete this shared memory? No conversation will retrieve it afterward. Existing chat text will be kept."); _confirmBox.Visibility = Visibility.Visible;
    }
    private void CancelDelete() { _deleteId = null; _confirmBox.Visibility = Visibility.Collapsed; }
    private void ConfirmDelete()
    {
        if (_deleteId == null) return;
        try { _store.DeleteMemory(_deleteId); CancelDelete(); NewMemory(); RefreshList(); SetStatus(L.T("已删除共享记忆。已有对话中的文字没有改动。", "Shared memory deleted. Text in existing conversations is unchanged.")); }
        catch (Exception) { SetStatus(L.T("删除失败，请重试。", "Deletion failed. Please retry."), true); }
    }
    private void SetStatus(string message, bool error = false) { _status.Text = message; _status.Foreground = Ui.Brush(error ? "#A44F39" : "#6C8261"); }
}
