using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

internal sealed partial class CodexDashboard : Window
{
    private readonly CodexMonitor _monitor;
    private readonly DesktopSettings _settings;
    private readonly Grid _root = new();
    private readonly TextBlock _connection = Ui.Text(L.T("正在连接", "Connecting"), 12, "#42644A", true);
    private readonly Border _connectionBadge = new();
    private readonly TextBlock _updated = Ui.Text(L.T("等待第一次刷新", "Waiting for the first refresh"), 11, "#81917F");
    private readonly Button _refresh = new();
    private readonly Button _forget = new();
    private readonly CheckBox _messagesAccess = new();
    private readonly ComboBox _buckets = new();
    private readonly TextBlock _bucketHint = Ui.Text(L.T("额度概览", "Usage overview"), 12, "#71826D");
    private readonly TextBlock _balance = Ui.Text("—", 30, "#244A34", true);
    private readonly TextBlock _balanceDetail = Ui.Text(L.T("等待连接 Codex", "Waiting to connect to Codex"), 11, "#82917C");
    private readonly TextBlock _resetCredits = Ui.Text("", 10, "#82917C");
    private readonly MeterCard _weekly = new(L.T("本周剩余", "Weekly remaining"));
    private readonly MeterCard _fiveHours = new(L.T("5 小时剩余", "5-hour remaining"));
    private readonly Border _notice = new();
    private readonly TextBlock _noticeText = Ui.Text("", 12, "#78623D");
    private readonly TextBox _search = new();
    private readonly ComboBox _filter = new();
    private readonly ComboBox _sourceFilter = new() { Name = "TaskSourceFilter" };
    private readonly TextBlock _sourceHint = Ui.Text("", 10, "#7D8C75");
    private readonly TextBlock _taskCount = Ui.Text(L.T("0 项", "0 tasks"), 11, "#869280");
    private readonly StackPanel _taskList = new();
    private readonly ScrollViewer _taskScroll = new();
    private readonly TextBlock _conversationTitle = Ui.Text(L.T("选一项任务，看看进展", "Select a task to see its progress"), 18, "#264934", true);
    private readonly TextBlock _conversationMeta = Ui.Text(L.T("只显示用户与助手消息", "Only user and assistant messages are shown"), 11, "#85917D");
    private readonly StackPanel _messages = new();
    private readonly ScrollViewer _messageScroll = new();
    private readonly TextBlock _conversationError = Ui.Text("", 12, "#9A6840");
    private readonly Button _older = new();
    private readonly CheckBox _notifications = new();
    private readonly ComboBox _interval = new();
    private readonly Button _settingsButton = new() { Name = "DashboardSettings", Content = L.T("设置", "Settings") };
    private readonly Action? _openSettings;
    private bool _rendering, _closed, _loadingOlder;
    private string? _bucketId, _uiError, _lastConversationTask;
    private string _taskRenderKey = "", _messageRenderKey = "";

    public CodexDashboard(CodexMonitor monitor, DesktopSettings settings, Action openSkins, PetWindow? speechHost = null,
        Action? openSettings = null)
    {
        _monitor = monitor; _settings = settings; _openSettings = openSettings;
        Title = AppVersion.WindowTitle(L.T("Codex 工作看板", "Codex work dashboard"));
        Width = 1180; Height = 850; MinWidth = 1000; MinHeight = 720;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("#F4F6F0");
        _root.Background = Background; _root.Margin = new Thickness(28, 24, 28, 20);
        for (int i = 0; i < 5; i++)
            _root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        _rendering = true;
        BuildHeader(openSkins); BuildUsage(); BuildNotice(); BuildWorkspace(); BuildPreferences();
        Content = _root; _rendering = false;
        _monitor.Changed += MonitorChanged;
        Closed += (_, _) =>
        {
            _closed = true; _monitor.Changed -= MonitorChanged; _monitor.ClearSelection();
        };
        Loaded += async (_, _) => { if (_monitor.Snapshot.LastUpdated is null) await RunActionAsync(() => _monitor.RefreshAsync(true)); };
        KeyDown += async (_, e) =>
        {
            if (e.Key != Key.F5) return;
            e.Handled = true; await RunActionAsync(() => _monitor.RefreshAsync(true));
        };
        Render();
    }

    private void BuildHeader(Action openSkins)
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var heading = new StackPanel();
        var eyebrow = Ui.Text("AI PET  /  CODEX  ·  " + AppVersion.Label, 10, "#61715D", true);
        eyebrow.Name = "AppVersionLabel";
        eyebrow.ToolTip = AppVersion.ProductLabel;
        AutomationProperties.SetName(eyebrow, L.T("AI Pet 版本 ", "AI Pet version ") + AppVersion.Number);
        eyebrow.Margin = new Thickness(1, 0, 0, 5); heading.Children.Add(eyebrow);
        heading.Children.Add(Ui.Text(L.T("工作看板", "Work dashboard"), 28, "#203E2C", true));
        var intro = Ui.Text(L.T("小伙伴陪你工作，进展随时看见。", "A companion by your side, with progress in view."), 12, "#84917C");
        intro.Margin = new Thickness(1, 7, 0, 0); heading.Children.Add(intro); header.Children.Add(heading);
        var actions = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        _settingsButton.Margin = new Thickness(0, 0, 8, 0);
        _settingsButton.IsEnabled = _openSettings != null;
        _settingsButton.Click += (_, _) => _openSettings?.Invoke();
        AutomationProperties.SetName(_settingsButton, L.T("设置", "Settings"));
        buttons.Children.Add(_settingsButton);
        var skins = Ui.Button(L.T("给桌宠换装", "Change character skin"), (_, _) => openSkins());
        skins.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(skins);
        _refresh.Content = L.T("立即刷新", "Refresh now"); _refresh.Background = Ui.Brush("#28563C"); _refresh.Foreground = Brushes.White;
        _refresh.BorderThickness = new Thickness(0); _refresh.MinWidth = 105; _refresh.ToolTip = L.T("刷新连接、额度与任务（F5）", "Refresh connection, usage, and tasks (F5)");
        _refresh.Click += async (_, _) => await RunActionAsync(() => _monitor.ConnectionEnabled ? _monitor.RefreshAsync(true) : _monitor.ResumeConnectionAsync());
        buttons.Children.Add(_refresh); actions.Children.Add(buttons);
        _forget.Content = L.T("断开并清除连接", "Disconnect and forget"); _forget.Margin = new Thickness(8, 0, 0, 0);
        _forget.ToolTip = L.T("停止读取并清除本机连接记录；再次连接需要重新配对", "Stop reading and clear the local connection record; reconnecting requires pairing again");
        _forget.Click += async (_, _) => await RunActionAsync(() => _monitor.ForgetConnectionAsync());
        buttons.Children.Add(_forget);
        var status = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        _connectionBadge.CornerRadius = new CornerRadius(8); _connectionBadge.Padding = new Thickness(9, 4, 9, 4); _connectionBadge.Child = _connection;
        status.Children.Add(_connectionBadge);
        _updated.Margin = new Thickness(10, 0, 0, 0); _updated.VerticalAlignment = VerticalAlignment.Center; status.Children.Add(_updated);
        actions.Children.Add(status); Grid.SetColumn(actions, 1); header.Children.Add(actions); _root.Children.Add(header);
    }

    private void BuildUsage()
    {
        var area = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        var caption = new Grid { Margin = new Thickness(0, 0, 0, 9) };
        _bucketHint.VerticalAlignment = VerticalAlignment.Center; caption.Children.Add(_bucketHint);
        ConfigureCombo(_buckets); _buckets.MinWidth = 150; _buckets.MaxWidth = 270; _buckets.HorizontalAlignment = HorizontalAlignment.Right;
        _buckets.DisplayMemberPath = nameof(BucketOption.Label); _buckets.SelectedValuePath = nameof(BucketOption.Id);
        _buckets.SelectionChanged += (_, _) => { if (_rendering) return; _bucketId = (_buckets.SelectedItem as BucketOption)?.Id; RenderUsage(); };
        caption.Children.Add(_buckets); area.Children.Add(caption);
        var cards = new Grid();
        for (int i = 0; i < 3; i++) cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var body = new StackPanel(); body.Children.Add(Ui.Text(L.T("Credits 余额", "Credits balance"), 12, "#6C8264", true));
        _balance.Margin = new Thickness(0, 8, 0, 5); _balance.TextTrimming = TextTrimming.CharacterEllipsis; body.Children.Add(_balance);
        body.Children.Add(_balanceDetail); _resetCredits.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(_resetCredits);
        var balanceCard = Card(body, "#E8F0E2"); balanceCard.Margin = new Thickness(0, 0, 10, 0); balanceCard.MinHeight = 142; cards.Children.Add(balanceCard);
        _weekly.Container.Margin = new Thickness(0, 0, 10, 0); Grid.SetColumn(_weekly.Container, 1); cards.Children.Add(_weekly.Container);
        Grid.SetColumn(_fiveHours.Container, 2); cards.Children.Add(_fiveHours.Container); area.Children.Add(cards);
        Grid.SetRow(area, 1); _root.Children.Add(area);
    }

    private void BuildNotice()
    {
        _noticeText.Name = "ConnectionNotice";
        _notice.CornerRadius = new CornerRadius(10); _notice.Background = Ui.Brush("#F4EDDB");
        _notice.Padding = new Thickness(13, 10, 13, 10); _notice.Margin = new Thickness(0, 0, 0, 12);
        _noticeText.TextWrapping = TextWrapping.Wrap; _noticeText.MaxHeight = 48; _notice.Child = _noticeText;
        Grid.SetRow(_notice, 2); _root.Children.Add(_notice);
    }


    private void BuildWorkspace()
    {
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(315) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        workspace.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new Grid();
        left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); left.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var title = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        title.Children.Add(Ui.Text(L.T("最近任务", "Recent tasks"), 17, "#284B35", true));
        _taskCount.HorizontalAlignment = HorizontalAlignment.Right; _taskCount.VerticalAlignment = VerticalAlignment.Center; title.Children.Add(_taskCount); left.Children.Add(title);
        var filters = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        var selectors = new Grid();
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        selectors.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ConfigureCombo(_sourceFilter);
        _sourceFilter.DisplayMemberPath = nameof(TaskSourceOption.Label);
        _sourceFilter.SelectedValuePath = nameof(TaskSourceOption.Filter);
        foreach (var option in new[] {
            new TaskSourceOption(DashboardTaskFilter.Codex, "Codex"),
            new TaskSourceOption(DashboardTaskFilter.Dot, "Dot"),
            new TaskSourceOption(DashboardTaskFilter.All, L.T("全部", "All")) }) _sourceFilter.Items.Add(option);
        _sourceFilter.SelectedValue = _monitor.DashboardFilter;
        _sourceFilter.ToolTip = L.T("筛选看板任务；桌宠与气泡始终只跟随 Codex", "Filter dashboard tasks; the pet and bubbles always follow Codex only");
        AutomationProperties.SetName(_sourceFilter, L.T("任务来源：Codex / Dot / 全部", "Task source: Codex / Dot / All"));
        _sourceFilter.SelectionChanged += (_, _) =>
        {
            if (_rendering || _sourceFilter.SelectedItem is not TaskSourceOption option) return;
            _monitor.SetDashboardFilter(option.Filter);
        };
        selectors.Children.Add(_sourceFilter);
        ConfigureCombo(_filter);
        foreach (string value in new[] { L.T("所有状态", "All states"), L.T("运行中", "Running"), L.T("需要关注", "Needs attention"), L.T("已空闲", "Idle") }) _filter.Items.Add(value);
        _filter.SelectedIndex = 0; _filter.SelectionChanged += (_, _) => { if (!_rendering) RenderTasks(); };
        Grid.SetColumn(_filter, 2); selectors.Children.Add(_filter);
        filters.Children.Add(selectors);
        _sourceHint.Name = "TaskSourceHint";
        _sourceHint.TextWrapping = TextWrapping.Wrap;
        _sourceHint.Margin = new Thickness(0, 7, 0, 10);
        filters.Children.Add(_sourceHint);
        var label = Ui.Text(L.T("最近 50 条记录及置顶任务 · 搜索", "Latest 50 records and pinned tasks · Search"), 10, "#86947F"); label.Margin = new Thickness(0, 0, 0, 5); filters.Children.Add(label);
        _search.FontFamily = new FontFamily("Microsoft YaHei UI"); _search.FontSize = 12; _search.Padding = new Thickness(9, 7, 9, 7);
        _search.BorderBrush = Ui.Brush("#DDE5D6"); _search.Foreground = Ui.Brush("#38523B"); _search.Background = Ui.Brush("#FAFBF7");
        _search.ToolTip = L.T("按任务标题或项目名搜索", "Search by task title or project"); AutomationProperties.SetName(_search, L.T("搜索任务标题或项目名", "Search task titles or project names"));
        _search.TextChanged += (_, _) => { if (!_rendering) RenderTasks(); }; filters.Children.Add(_search);
        Grid.SetRow(filters, 1); left.Children.Add(filters);
        _taskScroll.Content = _taskList; _taskScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _taskScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _taskScroll.Padding = new Thickness(0, 0, 5, 0); Grid.SetRow(_taskScroll, 2); left.Children.Add(_taskScroll);
        var taskCard = Card(left); taskCard.Padding = new Thickness(18); workspace.Children.Add(taskCard);
        var right = new Grid();
        for (int i = 0; i < 4; i++) right.RowDefinitions.Add(new RowDefinition { Height = i == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        _conversationTitle.TextWrapping = TextWrapping.Wrap; _conversationTitle.MaxHeight = 54; heading.Children.Add(_conversationTitle);
        _conversationMeta.Margin = new Thickness(0, 7, 0, 0); heading.Children.Add(_conversationMeta); right.Children.Add(heading);
        _conversationError.TextWrapping = TextWrapping.Wrap; _conversationError.Margin = new Thickness(0, 0, 0, 10); _conversationError.MaxHeight = 50;
        Grid.SetRow(_conversationError, 1); right.Children.Add(_conversationError);
        _messageScroll.Content = _messages; _messageScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; _messageScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _messageScroll.Padding = new Thickness(0, 0, 8, 0); Grid.SetRow(_messageScroll, 2); right.Children.Add(_messageScroll);
        _older.Content = L.T("加载更早的消息", "Load older messages"); _older.Margin = new Thickness(0, 12, 0, 0); _older.HorizontalAlignment = HorizontalAlignment.Center; _older.Padding = new Thickness(20, 7, 20, 7);
        _older.Click += async (_, _) =>
        {
            if (_loadingOlder) return;
            _loadingOlder = true; RenderConversation();
            try { await RunActionAsync(() => _monitor.LoadOlderAsync()); }
            finally { _loadingOlder = false; if (!_closed) RenderConversation(); }
        };
        Grid.SetRow(_older, 3); right.Children.Add(_older);
        var conversationCard = Card(right); conversationCard.Padding = new Thickness(21, 18, 18, 17);
        Grid.SetColumn(conversationCard, 2); workspace.Children.Add(conversationCard); Grid.SetRow(workspace, 3); _root.Children.Add(workspace);
    }

    private void BuildPreferences()
    {
        var footer = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(245) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(235) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _notifications.Content = L.T("Codex 任务完成时提醒", "Task notifications"); _notifications.Margin = new Thickness(0, 0, 28, 0); _notifications.VerticalAlignment = VerticalAlignment.Center;
        _notifications.Checked += (_, _) => ChangeNotifications(true); _notifications.Unchecked += (_, _) => ChangeNotifications(false); footer.Children.Add(_notifications);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var label = Ui.Text(L.T("任务刷新", "Task refresh"), 12, "#647A5C"); label.VerticalAlignment = VerticalAlignment.Center; label.Margin = new Thickness(0, 0, 9, 0); row.Children.Add(label);
        ConfigureCombo(_interval); _interval.MinWidth = 90;
        foreach (int seconds in new[] { 5, 10, 15, 30, 60, 120 }) _interval.Items.Add(new IntervalOption(seconds));
        _interval.SelectionChanged += (_, _) =>
        {
            if (_rendering || _interval.SelectedItem is not IntervalOption option) return;
            try { _monitor.SetRefreshSeconds(option.Seconds); }
            catch (Exception ex) { _uiError = L.T("暂时无法保存刷新设置：", "Could not save refresh settings: ") + ex.Message; Render(); }
        };
        row.Children.Add(_interval); Grid.SetColumn(row, 1); footer.Children.Add(row);
        var note = Ui.Text(L.T("额度每 60 秒刷新 · F5 立即刷新\n关闭看板后，桌宠继续留意任务进展", "Usage refreshes every 60 seconds · F5 to refresh now\nThe pet keeps following tasks after you close this dashboard"), 10, "#8B9783");
        note.TextAlignment = TextAlignment.Right; note.VerticalAlignment = VerticalAlignment.Center; note.LineHeight = 17;
        Grid.SetColumn(note, 2); footer.Children.Add(note);
        var preferences = new StackPanel(); preferences.Children.Add(footer);
        var privacy = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        _messagesAccess.Content = L.T("读取对话内容", "Read conversation content"); _messagesAccess.VerticalAlignment = VerticalAlignment.Center;
        _messagesAccess.Checked += (_, _) => ChangeMessageAccess(true);
        _messagesAccess.Unchecked += (_, _) => ChangeMessageAccess(false);
        privacy.Children.Add(_messagesAccess);
        var privacyHint = Ui.Text(L.T("默认关闭；开启后可查看消息并核验完成提醒。关闭时仅查询额度和任务列表。", "Off by default. Enable to view messages and verify completion notices. When off, only usage and task lists are queried."), 10, "#71826D");
        privacyHint.Margin = new Thickness(14, 0, 0, 0); privacyHint.VerticalAlignment = VerticalAlignment.Center;
        privacyHint.TextWrapping = TextWrapping.Wrap; privacy.Children.Add(privacyHint); preferences.Children.Add(privacy);
        Grid.SetRow(preferences, 4); _root.Children.Add(preferences);
    }

    private void ChangeMessageAccess(bool enabled)
    {
        if (_rendering) return;
        _monitor.SetMessageAccess(enabled); Render();
    }

    private void ChangeNotifications(bool enabled)
    {
        if (_rendering) return;
        try { _monitor.SetNotifications(enabled); }
        catch (Exception ex) { _uiError = L.T("暂时无法保存提醒设置：", "Could not save notification settings: ") + ex.Message; Render(); }
    }

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_closed) return;
        _uiError = null;
        try { await action(); }
        catch (Exception ex) { _uiError = ConnectionNotice(_monitor.Snapshot); Program.Log(ex); }
        if (!_closed) Render();
    }

    private void MonitorChanged()
    {
        if (_closed) return;
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(new Action(MonitorChanged)); return; }
        if (_monitor.Snapshot.Connected && !_monitor.Snapshot.Refreshing) _uiError = null;
        Render();
    }

    private void Render()
    {
        if (_closed) return;
        _rendering = true;
        try
        {
            var state = _monitor.Snapshot;
            _connection.Text = !_monitor.ConnectionEnabled ? L.T("○ 连接已关闭", "○ Connection off") : state.Connected ? L.T("● 已连接 Codex", "● Connected to Codex") : state.Refreshing ? L.T("◌ 正在连接", "◌ Connecting") : L.T("○ 等待 Codex", "○ Waiting for Codex");
            _connection.Foreground = Ui.Brush(state.Connected ? "#3D6948" : "#8B795A");
            _connectionBadge.Background = Ui.Brush(state.Connected ? "#E3EDDC" : "#ECEADF"); _connectionBadge.ToolTip = state.StatusText;
            _updated.Text = state.LastUpdated is { } at ? L.T("最近刷新 ", "Last refreshed ") + at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture) : L.T("等待第一次刷新", "Waiting for the first refresh");
            _refresh.IsEnabled = !state.Refreshing; _refresh.Content = !_monitor.ConnectionEnabled ? L.T("重新连接", "Reconnect") : state.Refreshing ? L.T("刷新中…", "Refreshing…") : L.T("立即刷新", "Refresh now");
            _forget.IsEnabled = _monitor.ConnectionEnabled || state.Connected;
            _messagesAccess.IsChecked = _monitor.MessagesEnabled; _messagesAccess.IsEnabled = _monitor.ConnectionEnabled;
            _notifications.IsEnabled = _monitor.MessagesEnabled;
            _notifications.ToolTip = _monitor.MessagesEnabled ? L.T("依据实际任务结果提醒", "Notify based on actual task results") : L.T("开启对话读取后才能核验任务完成；当前不会读取详情", "Enable message reading to verify completion; details are not currently read");
            _notifications.IsChecked = _settings.NotificationsEnabled;
            _interval.SelectedItem = _interval.Items.Cast<IntervalOption>().FirstOrDefault(x => x.Seconds == _settings.RefreshSeconds);
            _sourceFilter.SelectedValue = _monitor.DashboardFilter;
            RenderUsage();
            var notice = _uiError;
            if (string.IsNullOrWhiteSpace(notice) && !state.Connected)
            {
                notice = ConnectionNotice(state);
                if (!string.IsNullOrWhiteSpace(state.StatusText)) notice += "  " + state.StatusText;
            }
            else if (string.IsNullOrWhiteSpace(notice) && !string.IsNullOrWhiteSpace(state.Error))
                notice = L.T("部分信息暂未刷新，已保留最近内容。", "Some information has not refreshed. The latest available content is retained.");
            _noticeText.Text = notice ?? ""; _notice.ToolTip = state.Error;
            _notice.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
            RenderTasks(); RenderConversation();
        }
        finally { _rendering = false; }
    }

    private string ConnectionNotice(CodexSnapshot state)
    {
        return _monitor.PairingState switch
        {
            CodexPairingState.Revoked => L.T("连接已清除。请在 Codex 中重新配对，然后点击「重新连接」。", "Connection record cleared. Pair again in Codex, then click Reconnect."),
            CodexPairingState.Missing => L.T("未找到配对记录。请按 Codex首次连接.txt 完成首次配对。", "No pairing record found. Follow Codex首次连接.txt to pair for the first time."),
            CodexPairingState.Expired => L.T("连接记录已过期。请按 Codex首次连接.txt 主动重新配对。", "The connection record has expired. Follow Codex首次连接.txt to pair again explicitly."),
            CodexPairingState.Unavailable => _monitor.ConnectionRequested
                ? L.T("连接记录暂时无法读取，已暂停请求，将自动重试；无需重新配对。", "The connection record is temporarily unreadable. Requests are paused and will retry automatically; no new pairing is needed.")
                : L.T("连接记录暂时无法读取，请稍后点击「重新连接」。", "The connection record is temporarily unreadable. Click Reconnect later."),
            CodexPairingState.Invalid => L.T("连接记录未通过安全校验，已暂停请求。请检查文件权限及记录有效期；不会绕过保护。", "The connection record failed security checks. Requests are paused. Check file permissions and expiration; protection will not be bypassed."),
            _ when !_monitor.ConnectionRequested => L.T("连接已关闭。点击「重新连接」可尝试使用已有配对。", "Connection is off. Click Reconnect to try using the existing pairing."),
            _ when state.Connected => L.T("这次操作没有完成，请稍后重试。", "This operation did not finish. Please retry shortly."),
            _ when state.LastUpdated is null => L.T("正在连接 Codex。请确认 App 已打开并联网，已有配对会自动重试。", "Connecting to Codex. Make sure the app is open and online; existing pairing will retry automatically."),
            _ => L.T("连接暂时中断，当前保留上次读取的内容。正在自动重试，也可点击「立即刷新」。", "Connection temporarily interrupted. Last-read data is retained. Retrying automatically; you can also click Refresh now.")
        };
    }

    private void RenderUsage()
    {
        var state = _monitor.Snapshot;
        var chosen = state.Buckets.FirstOrDefault(x => x.Id == _bucketId) ?? state.Buckets.FirstOrDefault();
        _bucketId = chosen?.Id;
        string options = string.Join("|", state.Buckets.Select(x => x.Id + "=" + x.Name));
        if (!string.Equals(_buckets.Tag as string, options, StringComparison.Ordinal))
        {
            bool previous = _rendering; _rendering = true; _buckets.Items.Clear();
            foreach (var bucket in state.Buckets) _buckets.Items.Add(new BucketOption(bucket.Id, string.IsNullOrWhiteSpace(bucket.Name) ? bucket.Id : bucket.Name));
            _buckets.Tag = options; _buckets.SelectedValue = _bucketId; _rendering = previous;
        }
        _buckets.Visibility = state.Buckets.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _bucketHint.Text = L.T("额度概览", "Usage overview") + (string.IsNullOrWhiteSpace(chosen?.Plan) ? "" : "  ·  " + chosen.Plan);
        _bucketHint.ToolTip = state.UsageUpdatedAt is { } usageAt ? L.T("额度更新时间：", "Usage updated: ") + usageAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) : L.T("等待额度数据", "Waiting for usage data");
        string credits = L.T("暂不可用", "Unavailable");
        if (chosen?.Unlimited == true) credits = L.T("不限额", "Unlimited");
        else if (double.TryParse(chosen?.CreditBalance, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value))
            credits = Math.Max(0, Math.Ceiling(value)).ToString("N0", CultureInfo.InvariantCulture) + " credits";
        _balance.Text = credits; _balance.FontSize = credits.Length > 16 ? 23 : 30; _balance.ToolTip = chosen?.CreditBalance;
        _balanceDetail.Text = chosen is null ? L.T("连接后显示当前余额", "Connect to view the current balance") : chosen.HasCredits == false ? L.T("当前无可用 Credits", "No credits currently available") : L.T("可用 credits", "Available credits");
        _resetCredits.Text = state.ResetCreditsAvailable is { } resets ? L.T("可用额度重置  ", "Available usage resets: ") + resets + L.T(" 次", " remaining") : L.T("额度重置次数暂未提供", "Usage reset count not provided");
        _weekly.Set(chosen?.Windows.FirstOrDefault(x => x.DurationMinutes == 10080));
        _fiveHours.Set(chosen?.Windows.FirstOrDefault(x => x.DurationMinutes == 300));
    }


    private void RenderTasks()
    {
        var state = _monitor.Snapshot;
        var sourceTasks = _monitor.DashboardTasks;
        string query = _search.Text.Trim();
        int unknown = state.UnknownTasks.Count;
        _sourceHint.Text = L.T("桌宠与气泡仅跟随 Codex。", "The pet and bubbles follow Codex only. ") + (unknown > 0
            ? L.T($"还有 {unknown} 项来源待确认，可切换到「全部」，右键任务归类。", $"The source of {unknown} task(s) is unconfirmed. Switch to All, then right-click a task to classify it.")
            : L.T("右键任务可调整来源归类。", "Right-click a task to change its source classification."));
        var filtered = sourceTasks.Where(task =>
            (query.Length == 0 || task.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || (task.ProjectName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)) &&
            (_filter.SelectedIndex switch { 1 => task.IsRunning, 2 => task.NeedsAttention, 3 => task.Status is "idle" or "completed", _ => true })).ToList();
        _taskCount.Text = query.Length > 0 || _filter.SelectedIndex > 0 ? filtered.Count + " / " + sourceTasks.Count + L.T(" 项", " tasks") : filtered.Count + L.T(" 项", " tasks");
        string selected = _monitor.SelectedTask?.Key ?? "";
        string key = selected + "|" + query + "|" + _filter.SelectedIndex + "|" + _monitor.DashboardFilter + "|" + state.Connected + "|" +
            string.Join("\n", filtered.Select(x => x.Key + "|" + x.Title + "|" + x.Status + "|" + x.ProjectName + "|" + x.Pinned + "|" + x.Source + "|" + x.SourceIsManual));
        if (key == _taskRenderKey) return;
        _taskRenderKey = key;
        double offset = _taskScroll.VerticalOffset;
        _taskList.Children.Clear();
        if (filtered.Count == 0)
        {
            _taskList.Children.Add(EmptyState(query.Length > 0 || _filter.SelectedIndex > 0 ? L.T("没有匹配的任务", "No matching tasks") : L.T("此来源暂无任务", "No tasks from this source"),
                query.Length > 0 || _filter.SelectedIndex > 0 ? L.T("试试其它关键词，或切换到所有状态。", "Try different keywords or switch to All states.") : unknown > 0
                    ? L.T("切换到「全部」可查看来源待确认的任务，右键可归入 Codex 或 Dot。", "Switch to All to see tasks with unconfirmed sources. Right-click to classify them as Codex or Dot.")
                    : state.Connected ? L.T("可以切换任务来源查看其它记录。", "Switch the task source to see other records.") : L.T("连接 Codex 后，最近的任务会显示在这里。", "Recent tasks will appear here after connecting to Codex.")));
            return;
        }
        foreach (var task in filtered)
        {
            bool isSelected = task.Key == selected;
            var body = new StackPanel();
            var top = new Grid();
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            top.Children.Add(Ui.Text((task.IsRunning ? "●  " : task.NeedsAttention ? "!  " : "○  ") + task.StatusLabel, 10,
                task.NeedsAttention ? "#A2793C" : task.IsRunning ? "#497447" : "#8B9681", true));
            var sourceLabel = Ui.Text(TaskSourcePolicy.SourceLabel(task) + (task.Pinned ? L.T(" · 置顶", " · Pinned") : ""), 9, "#8B9A80");
            sourceLabel.HorizontalAlignment = HorizontalAlignment.Right; sourceLabel.Margin = new Thickness(7, 0, 0, 0);
            Grid.SetColumn(sourceLabel, 1); top.Children.Add(sourceLabel);
            body.Children.Add(top);
            var label = Ui.Text(task.Title, 13, isSelected ? "#244B31" : "#4B6046", true);
            label.TextWrapping = TextWrapping.Wrap; label.TextTrimming = TextTrimming.CharacterEllipsis; label.MaxHeight = 42;
            label.Margin = new Thickness(0, 7, 0, 0); body.Children.Add(label);
            if (!string.IsNullOrWhiteSpace(task.ProjectName))
            {
                var project = Ui.Text(task.ProjectName, 10, "#8B9782"); project.TextTrimming = TextTrimming.CharacterEllipsis;
                project.Margin = new Thickness(0, 6, 0, 0); body.Children.Add(project);
            }
            var button = new Button
            {
                Content = body, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(12, 11, 12, 11), Margin = new Thickness(0, 0, 0, 8),
                BorderThickness = new Thickness(isSelected ? 1.5 : 1),
                BorderBrush = Ui.Brush(isSelected ? "#8EA582" : "#EBEFE5"),
                Background = Ui.Brush(isSelected ? "#EDF3E7" : "#FBFCF8"), ToolTip = task.Title
            };
            button.ContextMenu = BuildTaskSourceMenu(task);
            AutomationProperties.SetName(button, task.Title + "，" + TaskSourcePolicy.SourceLabel(task) + "，" + task.StatusLabel);
            button.Click += async (_, _) => await RunActionAsync(() => _monitor.SelectTaskAsync(task));
            _taskList.Children.Add(button);
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => { if (!_closed) _taskScroll.ScrollToVerticalOffset(offset); }));
    }

    private ContextMenu BuildTaskSourceMenu(CodexTaskInfo task)
    {
        var menu = new ContextMenu();
        void Add(string label, TaskSource? source, bool selected)
        {
            var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = selected };
            item.Click += (_, _) =>
            {
                try { _uiError = null; _monitor.SetTaskSourceOverride(task, source); }
                catch (Exception ex) { _uiError = L.T("任务归类暂未保存，请稍后重试。", "Could not save task classification. Please retry shortly."); Program.Log(ex); Render(); }
            };
            menu.Items.Add(item);
        }
        Add(L.T("归入 Codex", "Mark as Codex"), TaskSource.Codex, task.SourceIsManual && task.Source == TaskSource.Codex);
        Add(L.T("归入 Dot", "Mark as Dot"), TaskSource.Dot, task.SourceIsManual && task.Source == TaskSource.Dot);
        menu.Items.Add(new Separator());
        Add(L.T("自动识别来源", "Detect source automatically"), null, !task.SourceIsManual);
        return menu;
    }

    private void RenderConversation()
    {
        var task = _monitor.SelectedTask;
        var page = _monitor.Conversation;
        bool pageMatches = task is not null && page?.TaskKey == task.Key;
        _conversationTitle.Text = task?.Title ?? L.T("选一项任务，看看进展", "Select a task to see its progress"); _conversationTitle.ToolTip = task?.Title;
        _conversationMeta.Text = !_monitor.MessagesEnabled ? L.T("对话读取已关闭", "Message reading is off") : task is null ? L.T("只显示用户与助手消息", "Only user and assistant messages are shown") : task.StatusLabel + "  ·  " + (_monitor.IsReading ? L.T("正在读取…", "Reading…") : L.T("用户与助手消息", "User and assistant messages"));
        string? error = _monitor.ConversationError;
        _conversationError.Text = string.IsNullOrWhiteSpace(error) ? "" : L.T("消息暂未读取成功，可再次点击任务重试。", "Could not read messages. Click the task again to retry.");
        _conversationError.ToolTip = error; _conversationError.Visibility = string.IsNullOrWhiteSpace(error) ? Visibility.Collapsed : Visibility.Visible;
        _older.Visibility = pageMatches && page!.HasMore ? Visibility.Visible : Visibility.Collapsed;
        _older.IsEnabled = _monitor.Snapshot.Connected && !_monitor.IsReading && !_loadingOlder;
        _older.Content = _loadingOlder ? L.T("正在加载…", "Loading…") : L.T("加载更早的消息", "Load older messages");
        string selectedKey = task?.Key ?? "";
        string renderKey = _monitor.MessagesEnabled + "|" + selectedKey + "|" + pageMatches + "|" + error + "|" +
            (pageMatches ? string.Join("\n\u001f", page!.Messages.Select(x => x.Id + "|" + x.Role + "|" + x.Phase + "|" + x.Text)) : _monitor.IsReading.ToString());
        if (renderKey == _messageRenderKey) return;
        _messageRenderKey = renderKey;
        bool changedTask = _lastConversationTask != selectedKey; _lastConversationTask = selectedKey;
        double oldOffset = _messageScroll.VerticalOffset, oldExtent = _messageScroll.ExtentHeight;
        bool wasAtBottom = _messageScroll.ScrollableHeight - oldOffset < 40, loadingOlder = _loadingOlder;
        _messages.Children.Clear();
        if (!_monitor.MessagesEnabled)
            _messages.Children.Add(EmptyState(L.T("消息读取默认关闭", "Message reading is off by default"), L.T("需要查看对话时，可在下方开启「读取对话内容」。关闭后会清除已显示的消息。", "To view conversations, enable Read conversation content below. Turning it off clears displayed messages.")));
        else if (task is null)
            _messages.Children.Add(EmptyState(L.T("让进展慢慢展开", "Follow the progress"), L.T("在左侧选一项任务，查看你与助手的对话。", "Select a task on the left to view your conversation with the assistant.")));
        else if (!pageMatches || page!.Messages.Count == 0)
            _messages.Children.Add(EmptyState(_monitor.IsReading ? L.T("正在读取消息…", "Reading messages…") : L.T("最近几轮暂无公开消息", "No visible messages in recent turns"),
                _monitor.IsReading ? L.T("稍等片刻，对话很快就到。", "Just a moment; the conversation is loading.") : L.T("可加载更早内容，查看此前的对话。", "Load older messages to see the earlier conversation.")));
        else
            foreach (var message in page!.Messages)
            {
                if (message.Role != "user" && message.Role != "assistant") continue;
                var body = new StackPanel(); bool isUser = message.Role == "user";
                var label = Ui.Text(isUser ? L.T("你", "You") : message.Phase == "commentary" ? L.T("助手 · 进展", "Assistant · Progress") : L.T("助手", "Assistant"), 10, isUser ? "#708768" : "#7C8D74", true);
                label.Margin = new Thickness(0, 0, 0, 8); body.Children.Add(label);
                var text = new TextBox
                {
                    Text = message.Text, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextWrapping = TextWrapping.Wrap,
                    AcceptsReturn = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
                    Foreground = Ui.Brush("#3F5140"), FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 12,
                    Padding = new Thickness(0), VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Cursor = Cursors.IBeam
                };
                // The read-only TextBox allows copying; let the surrounding transcript handle scrolling.
                text.PreviewMouseWheel += (_, e) =>
                {
                    double movement = SystemParameters.WheelScrollLines < 0
                        ? _messageScroll.ViewportHeight * Math.Sign(e.Delta)
                        : e.Delta / 120.0 * SystemParameters.WheelScrollLines * 20;
                    _messageScroll.ScrollToVerticalOffset(_messageScroll.VerticalOffset - movement);
                    e.Handled = true;
                };
                AutomationProperties.SetName(text, isUser ? L.T("用户消息", "User message") : L.T("助手消息", "Assistant message")); body.Children.Add(text);
                _messages.Children.Add(new Border
                {
                    Background = Ui.Brush(isUser ? "#EDF3E7" : "#F7F8F2"), BorderBrush = Ui.Brush(isUser ? "#E4EDDC" : "#EEF0E8"),
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(15, 12, 15, 13),
                    Margin = new Thickness(isUser ? 24 : 0, 0, isUser ? 0 : 20, 12), Child = body
                });
            }
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (_closed || _lastConversationTask != selectedKey) return;
            if (loadingOlder) _messageScroll.ScrollToVerticalOffset(oldOffset + Math.Max(0, _messageScroll.ExtentHeight - oldExtent));
            else if (changedTask || wasAtBottom) _messageScroll.ScrollToEnd();
            else _messageScroll.ScrollToVerticalOffset(oldOffset);
        }));
    }

    private static Border Card(UIElement child, string color = "#FFFFFF") => new()
    {
        Background = Ui.Brush(color), BorderBrush = Ui.Brush("#E5EADF"), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(15), Padding = new Thickness(19, 16, 19, 15), Child = child
    };

    private static StackPanel EmptyState(string title, string detail)
    {
        var box = new StackPanel { Margin = new Thickness(8, 38, 8, 24) };
        var icon = Ui.Text("◇", 28, "#B2C3A5"); icon.TextAlignment = TextAlignment.Center; box.Children.Add(icon);
        var heading = Ui.Text(title, 14, "#75896A", true); heading.TextAlignment = TextAlignment.Center;
        heading.TextWrapping = TextWrapping.Wrap; heading.Margin = new Thickness(0, 10, 0, 8); box.Children.Add(heading);
        var hint = Ui.Text(detail, 11, "#98A08C"); hint.TextAlignment = TextAlignment.Center; hint.TextWrapping = TextWrapping.Wrap; hint.LineHeight = 19;
        box.Children.Add(hint); return box;
    }

    private static void ConfigureCombo(ComboBox combo)
    {
        combo.FontFamily = new FontFamily("Microsoft YaHei UI"); combo.FontSize = 12; combo.Padding = new Thickness(8, 5, 8, 5);
        combo.Foreground = Ui.Brush("#46613E"); combo.Background = Brushes.White;
        combo.BorderBrush = Ui.Brush("#DCE5D6"); combo.BorderThickness = new Thickness(1);
    }

    public void SavePreview(string path)
    {
        UpdateLayout();
        // Capture the window root; it owns the child's arranged offset and surrounding margin.
        var image = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(ActualHeight)),
            96, 96, PixelFormats.Pbgra32);
        image.Render(this);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }
    private sealed record TaskSourceOption(DashboardTaskFilter Filter, string Label);
    private sealed record BucketOption(string Id, string Label);
    private sealed record IntervalOption(int Seconds)
    {
        public override string ToString() => Seconds >= 60 ? (Seconds / 60) + L.T(" 分钟", " min") : Seconds + L.T(" 秒", " s");
    }

    private sealed class MeterCard
    {
        public Border Container { get; }
        private readonly TextBlock _value = Ui.Text("—", 31, "#355437", true);
        private readonly TextBlock _detail = Ui.Text(L.T("暂未提供此额度窗口", "This usage window is not provided"), 10, "#83917B");
        private readonly ProgressBar _bar = new() { Minimum = 0, Maximum = 100, Height = 6, BorderThickness = new Thickness(0), Background = Ui.Brush("#E9EEE4"), Foreground = Ui.Brush("#7C9B68") };
        public MeterCard(string label)
        {
            var stack = new StackPanel(); stack.Children.Add(Ui.Text(label, 12, "#738368", true));
            _value.Margin = new Thickness(0, 8, 0, 7); stack.Children.Add(_value);
            _bar.Margin = new Thickness(0, 0, 0, 10); stack.Children.Add(_bar);
            _detail.TextWrapping = TextWrapping.Wrap; stack.Children.Add(_detail); Container = Card(stack); Container.MinHeight = 142;
        }
        public void Set(UsageWindowInfo? window)
        {
            double? remaining = window?.RemainingPercent;
            bool valid = remaining is { } n && double.IsFinite(n);
            _value.Text = valid ? remaining!.Value.ToString("0.#", CultureInfo.CurrentCulture) + "%" : L.T("未提供", "Not provided");
            _value.FontSize = valid ? 31 : 25; _bar.Value = valid ? Math.Clamp(remaining!.Value, 0, 100) : 0;
            _bar.Foreground = Ui.Brush(valid && remaining < 10 ? "#B58158" : "#739566");
            _value.Foreground = Ui.Brush(valid && remaining < 10 ? "#9B7446" : "#355437");
            _detail.Text = window is null ? L.T("当前账户未提供此额度窗口", "The current account does not provide this usage window") : window.ResetsAt is { } at
                ? L.T("重置于 ", "Resets at ") + at.ToLocalTime().ToString("M/d HH:mm", CultureInfo.CurrentCulture) : L.T("重置时间暂未提供", "Reset time not provided");
            Container.ToolTip = window is null ? L.T("缺失值不表示额度为零", "Missing data does not mean zero allowance") : L.T("窗口长度 ", "Window length: ") + window.DurationMinutes + L.T(" 分钟", " min") +
                (window.UsedPercent is { } used ? L.T(" · 已用 ", " · Used ") + used.ToString("0.#", CultureInfo.CurrentCulture) + "%" : "");
        }
    }
}
