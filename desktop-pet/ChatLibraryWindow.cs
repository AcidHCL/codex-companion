using System;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace DesktopPet;

internal sealed class ChatLibraryWindow : Window
{
    private readonly IChatMemoryStore _store;
    private readonly string _endpoint, _personaKey, _model;
    private string? _currentId;
    private readonly TextBox _search, _title;
    private readonly ListBox _list;
    private readonly TextBlock _status, _confirmation;
    private readonly Button _open, _rename, _delete;
    private readonly Border _confirmBox;
    private string? _deleteId;
    internal string? SelectedConversationId { get; private set; }

    internal ChatLibraryWindow(IChatMemoryStore store, string endpoint, string personaKey, string model, string? currentConversationId)
    {
        _store = store; _endpoint = endpoint; _personaKey = personaKey; _model = model; _currentId = currentConversationId;
        Title = AppVersion.WindowTitle(L.T("历史对话", "Conversation history")); Width = L.IsEnglish ? 560 : 470; Height = 575; MinWidth = 330; MinHeight = 480;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; Resources.MergedDictionaries.Add(Ui.CreateResources());
        var body = new Grid { Margin = new Thickness(19, 15, 19, 15) };
        for (int i = 0; i < 8; i++) body.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var heading = new DockPanel();
        var close = Ui.Button("×", (_, _) => Close()); close.Name = "ChatLibraryClose"; close.Width = 30; close.Height = 30; close.Padding = new Thickness(0);
        AutomationProperties.SetName(close, L.T("关闭历史对话", "Close conversation history")); DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        var label = Ui.Text(L.T("历史对话", "Conversation history"), 20, "#214332", true); label.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(label); body.Children.Add(heading);
        heading.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 1 && e.OriginalSource is TextBlock) DragMove(); };
        var note = Ui.Text(L.T("保存在此电脑 · 仅列出当前服务器、当前角色的对话", "Saved on this computer · Current server and character only"), 11, "#75866B"); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 8, 0, 11); Grid.SetRow(note, 1); body.Children.Add(note);
        _search = Input("ChatLibrarySearch", ""); _search.ToolTip = L.T("搜索对话标题", "Search conversation titles");
        AutomationProperties.SetName(_search, L.T("搜索对话标题", "Search conversation titles"));
        var searchFrame = Frame(_search); searchFrame.Margin = new Thickness(0, 0, 0, 10); Grid.SetRow(searchFrame, 2); body.Children.Add(searchFrame);
        _list = new ListBox { Name = "ChatLibraryList", Background = Brushes.White, BorderThickness = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(4), MinHeight = 30 };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.ItemContainerStyle = SelectionStyle(); AutomationProperties.SetName(_list, L.T("已保存的对话", "Saved conversations"));
        var listFrame = Frame(_list); Grid.SetRow(listFrame, 3); body.Children.Add(listFrame);
        var edit = new Grid { Margin = new Thickness(0, 11, 0, 0) }; edit.ColumnDefinitions.Add(new ColumnDefinition()); edit.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _title = Input("ChatLibraryTitle", ""); _title.MaxLength = 80; AutomationProperties.SetName(_title, L.T("对话标题", "Conversation title")); edit.Children.Add(Frame(_title));
        _rename = Ui.Button(L.T("改名", "Rename"), (_, _) => RenameSelected()); _rename.Name = "ChatLibraryRename"; _rename.Margin = new Thickness(7, 0, 0, 0); Grid.SetColumn(_rename, 1); edit.Children.Add(_rename); Grid.SetRow(edit, 4); body.Children.Add(edit);
        _confirmation = Ui.Text("", 11, "#874F3C"); _confirmation.TextWrapping = TextWrapping.Wrap;
        var confirmStack = new DockPanel { LastChildFill = true };
        var confirmActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 7, 0, 0) };
        var confirm = Ui.Button(L.T("确认删除", "Confirm deletion"), (_, _) => ConfirmDelete()); confirm.Name = "ChatLibraryConfirmDelete"; confirm.Foreground = Ui.Brush("#A34D3A"); confirmActions.Children.Add(confirm);
        var cancel = Ui.Button(L.T("取消", "Cancel"), (_, _) => CancelDelete()); cancel.Name = "ChatLibraryCancelDelete"; cancel.Margin = new Thickness(7, 0, 0, 0); confirmActions.Children.Add(cancel);
        DockPanel.SetDock(confirmActions, Dock.Bottom); confirmStack.Children.Add(confirmActions);
        confirmStack.Children.Add(new ScrollViewer { Content = _confirmation, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        // Confirmation replaces the flexible list area. It never pushes the
        // permanent Open/New controls below the bottom of a small window.
        _confirmBox = new Border { Child = confirmStack, Background = Ui.Brush("#FFF0E7"), Padding = new Thickness(10), CornerRadius = new CornerRadius(10), MinHeight = 75, Visibility = Visibility.Collapsed }; Grid.SetRow(_confirmBox, 3); body.Children.Add(_confirmBox);
        var footer = new StackPanel(); _status = Ui.Text("", 11, "#6C8261"); _status.Name = "ChatLibraryStatus"; _status.TextWrapping = TextWrapping.Wrap; _status.Margin = new Thickness(0, 9, 0, 8); footer.Children.Add(_status);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        var create = Ui.Button(L.T("新对话", "New conversation"), (_, _) => CreateNew()); create.Name = "ChatLibraryNew"; create.Margin = new Thickness(0, 0, 5, 0); actions.Children.Add(create);
        _delete = Ui.Button(L.T("删除", "Delete"), (_, _) => RequestDelete()); _delete.Name = "ChatLibraryDelete"; _delete.Margin = new Thickness(3, 0, 5, 0); Grid.SetColumn(_delete, 1); actions.Children.Add(_delete);
        _open = Ui.Button(L.T("打开对话", "Open conversation"), (_, _) => OpenSelected()); _open.Name = "ChatLibraryOpen"; _open.Background = Ui.Brush("#356446"); _open.Foreground = Brushes.White; _open.Margin = new Thickness(3, 0, 0, 0); Grid.SetColumn(_open, 2); actions.Children.Add(_open); footer.Children.Add(actions); Grid.SetRow(footer, 6); body.Children.Add(footer);
        var version = Ui.VersionLabel(false); version.HorizontalAlignment = HorizontalAlignment.Right; version.Margin = new Thickness(0, 9, 0, 0); Grid.SetRow(version, 7); body.Children.Add(version);
        Content = new Border { Background = Ui.Brush("#F7F9F2"), BorderBrush = Ui.Brush("#C9D6C0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = body };
        _search.TextChanged += (_, _) => RefreshList();
        _list.SelectionChanged += (_, _) => { _title.Text = Selected()?.Title ?? ""; CancelDelete(); UpdateActions(); };
        _list.MouseDoubleClick += (_, e) => { if (Selected() != null) { e.Handled = true; OpenSelected(); } };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; if (_confirmBox.Visibility == Visibility.Visible) CancelDelete(); else Close(); } };
        Loaded += (_, _) => FitWindow(this);
        RefreshList(currentConversationId);
    }

    private ChatConversation? Selected() => (_list.SelectedItem as ListBoxItem)?.Tag as ChatConversation;
    private void RefreshList(string? selectedId = null)
    {
        try
        {
            selectedId ??= Selected()?.Id ?? _currentId;
            string query = _search.Text.Trim();
            var all = _store.ListConversations(_endpoint, _personaKey);
            var matches = all.Where(item => query.Length == 0 || item.Title.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            _list.Items.Clear();
            foreach (var conversation in matches.Take(200))
            {
                var row = new StackPanel { Margin = new Thickness(7, 5, 7, 5) };
                var title = Ui.Text(conversation.Title, 13, "#294739", conversation.Id == _currentId); title.TextTrimming = TextTrimming.CharacterEllipsis; row.Children.Add(title);
                row.Children.Add(Ui.Text(conversation.UpdatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + (conversation.Id == _currentId ? L.T(" · 当前", " · Current") : "") + (conversation.HasDraft ? L.T(" · 草稿", " · Draft") : ""), 10, "#85917D"));
                var item = new ListBoxItem { Tag = conversation, Content = row, ToolTip = conversation.Title };
                _list.Items.Add(item); if (conversation.Id == selectedId) _list.SelectedItem = item;
            }
            if (_list.SelectedItem == null && _list.Items.Count > 0) _list.SelectedIndex = 0;
            _status.Text = matches.Count == 0 ? (query.Length > 0 ? L.T("没有匹配的对话。", "No matching conversations.") : L.T("这里还没有对话，点击“新对话”开始。", "No conversations yet. Click New conversation to start.")) : L.T($"共 {matches.Count} 个对话", $"{matches.Count} conversation(s)") + (matches.Count > 200 ? L.T("，显示最近 200 个；输入标题搜索更早的记录。", "; showing the latest 200. Search by title to find older records.") : L.T("。双击即可继续。", ". Double-click to continue."));
            _status.Foreground = Ui.Brush("#6C8261");
            UpdateActions();
        }
        catch (Exception) { SetError(L.T("读取对话失败，请检查本地存储是否可用。", "Could not read conversations. Check that local storage is available.")); }
    }

    private void UpdateActions() { bool selected = Selected() != null; _open.IsEnabled = selected; _rename.IsEnabled = selected; _delete.IsEnabled = selected; _title.IsEnabled = selected; }
    private void SetError(string message) { _status.Text = message; _status.Foreground = Ui.Brush("#A44F39"); }
    private void OpenSelected() { if (Selected() is { } item) { SelectedConversationId = item.Id; Close(); } }
    private void CreateNew()
    {
        try { SelectedConversationId = _store.CreateConversation(_endpoint, _personaKey, _model).Id; Close(); }
        catch (Exception) { SetError(L.T("新对话保存失败，未关闭当前记录。", "Could not save a new conversation. The current conversation remains open.")); }
    }
    private void RenameSelected()
    {
        if (Selected() is not { } item) return;
        string title = _title.Text.Trim(); if (title.Length == 0) { SetError(L.T("请填写对话标题。", "Enter a conversation title.")); return; }
        try { _store.RenameConversation(item.Id, title); RefreshList(item.Id); }
        catch (Exception) { SetError(L.T("标题保存失败，原记录保持不变。", "Could not save the title. The original record is unchanged.")); }
    }
    private void RequestDelete()
    {
        if (Selected() is not { } item) return;
        _deleteId = item.Id;
        _confirmation.Text = L.T("删除“", "Delete “") + item.Title + L.T("”？这会删除此对话的文字和图片，无法撤销。共享记忆会保留。", "”? Its messages and images will be deleted permanently. Shared memory will be kept.");
        _list.IsEnabled = false; _confirmBox.Visibility = Visibility.Visible;
    }
    private void CancelDelete() { _deleteId = null; _confirmBox.Visibility = Visibility.Collapsed; _list.IsEnabled = true; }
    private void ConfirmDelete()
    {
        if (_deleteId == null) return;
        string id = _deleteId;
        string? originalActive = null;
        bool preparedReplacement = false, deleted = false;
        try
        {
            // Prepare a replacement first, so an I/O failure cannot strand the
            // parent chat on a record that has just been deleted.
            string? replacement = null;
            if (id == _currentId)
            {
                originalActive = _store.GetActiveConversationId(_endpoint, _personaKey) ?? id;
                replacement = _store.ListConversations(_endpoint, _personaKey).FirstOrDefault(item => item.Id != id)?.Id
                    ?? _store.CreateConversation(_endpoint, _personaKey, _model).Id;
                preparedReplacement = true;
            }
            _store.DeleteConversation(id);
            deleted = true;
            if (id == _currentId)
            {
                _currentId = replacement; SelectedConversationId = replacement;
                if (replacement != null) _store.SetActiveConversation(replacement);
            }
            CancelDelete(); RefreshList(replacement);
        }
        catch (Exception)
        {
            // Creating the replacement sets the store's active pointer. If
            // deletion fails, restore it so restart still opens the old chat.
            if (!deleted && preparedReplacement && originalActive != null)
            {
                try { _store.SetActiveConversation(originalActive); } catch (Exception) { }
            }
            SetError(deleted ? L.T("记录已删除，但恢复其他对话时出错；关闭此窗口后会重试。", "The conversation was deleted, but restoring another one failed. Closing this window will retry.") : L.T("删除失败，请重试；未删除的记录仍然保留。", "Deletion failed. Please retry; records that were not deleted are still preserved."));
        }
    }

    internal static TextBox Input(string name, string text) => new() { Name = name, Text = text, FontSize = 13, Padding = new Thickness(9, 8, 9, 8), BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    internal static Border Frame(UIElement child) => new() { Child = child, Background = Brushes.White, BorderBrush = Ui.Brush("#D7E2CF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10) };
    internal static void FitWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var area = NativeMethods.GetWindowWorkArea(handle);
        double width = Math.Max(200, area.Width - 16), height = Math.Max(240, area.Height - 16);
        window.MinWidth = Math.Min(window.MinWidth, width); window.MinHeight = Math.Min(window.MinHeight, height);
        window.MaxWidth = width; window.MaxHeight = height;
        window.Width = Math.Min(window.Width, width); window.Height = Math.Min(window.Height, height);
        window.UpdateLayout(); NativeMethods.ClampWindowToWorkArea(handle);
    }
    internal static Style SelectionStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 2, 0, 2)));
        var border = new FrameworkElementFactory(typeof(Border)); border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8)); border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter)); border.AppendChild(presenter);
        style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(ListBoxItem)) { VisualTree = border }));
        var selected = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true }; selected.Setters.Add(new Setter(Control.BackgroundProperty, Ui.Brush("#E3EDD9"))); style.Triggers.Add(selected);
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true }; hover.Setters.Add(new Setter(Control.OpacityProperty, .82)); style.Triggers.Add(hover);
        return style;
    }
}
