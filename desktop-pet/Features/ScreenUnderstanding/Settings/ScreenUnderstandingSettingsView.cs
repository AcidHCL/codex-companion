using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet.ScreenUnderstanding;

internal sealed class ScreenUnderstandingSettingsView : UserControl, IDisposable
{
    private readonly IScreenUnderstandingSettingsHost _host;
    private readonly CheckBox _enabled, _text, _screenshots, _learn, _remote;
    private readonly ComboBox _interval, _category;
    private readonly TextBlock _status, _detail, _lastSummary, _feedback, _memoryDetail;
    private readonly Button _request, _correct, _delete, _clear;
    private readonly ListBox _memories;
    private bool _sync, _disposed;
    private string? _memoryFingerprint;

    internal ScreenUnderstandingSettingsView(IScreenUnderstandingSettingsHost host)
    {
        _host = host;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 4) };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };

        var intro = Card(stack, L.T("屏幕理解", "Screen understanding"));
        _enabled = Toggle(intro, "ScreenUnderstandingEnabled", L.T("启用", "Enabled"), value => Save(o => o with { Enabled = value }));
        AutomationProperties.SetName(_enabled, L.T("启用屏幕理解", "Enable screen understanding"));
        intro.Children.Add(Paragraph(L.T("需同时开启主动陪伴。", "Requires Proactive companion to be enabled.")));
        var state = intro;
        _status = Text("ScreenUnderstandingStatus", "", true); state.Children.Add(_status);
        _detail = Text("ScreenUnderstandingDetail", ""); state.Children.Add(_detail);
        _lastSummary = Text("ScreenUnderstandingLastSummary", ""); _lastSummary.Margin = new Thickness(0, 10, 0, 8); state.Children.Add(_lastSummary);
        _request = Button("ScreenUnderstandingRequest", L.T("切回窗口后查看", "Inspect when I return to the window"),
            () => Execute(_host.RequestNextWindowAnalysis));
        _request.ToolTip = L.T("先点击，再切回目标窗口并暂停输入片刻。", "Click, then return to the target window and pause input briefly.");
        _request.Margin = new Thickness(0, 12, 0, 0);
        _request.HorizontalAlignment = HorizontalAlignment.Left; state.Children.Add(_request);

        var read = Card(stack, L.T("读取内容", "Window content"));
        var sources = new Grid(); sources.ColumnDefinitions.Add(new ColumnDefinition()); sources.ColumnDefinitions.Add(new ColumnDefinition());
        _text = Toggle(sources, "ScreenUnderstandingReadText", L.T("窗口文字", "Visible text"), value => Save(o => o with { ReadWindowText = value }));
        _text.Margin = new Thickness(0, 7, 24, 7);
        _screenshots = Toggle(sources, "ScreenUnderstandingScreenshots", L.T("必要时截图", "Screenshots when needed"), value => Save(o => o with { UseScreenshots = value }));
        _screenshots.ToolTip = L.T("需要当前模型支持图片输入。", "Requires image input support in the selected model.");
        Grid.SetColumn(_screenshots, 1); read.Children.Add(sources);
        _remote = Toggle(read, "ScreenUnderstandingRemote", L.T("允许向远程服务发送窗口内容", "Allow window content to be sent to remote services"), value => Save(o => o with { AllowRemoteEndpoint = value }));
        read.Children.Add(Paragraph(L.T("云端 API 或其他电脑的模型需开启此项；所选文字和截图将发往该服务，可能产生费用。",
            "Required for cloud APIs or models on another computer. Selected text and images go to that service and may incur charges.")));
        var interval = new Grid { Margin = new Thickness(0, 8, 0, 5) };
        interval.ColumnDefinitions.Add(new ColumnDefinition()); interval.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        interval.Children.Add(Paragraph(L.T("最短分析间隔", "Minimum analysis interval")));
        _interval = new ComboBox { Name = "ScreenUnderstandingInterval", SelectedValuePath = "Tag", FontSize = 13, Padding = new Thickness(8, 5, 8, 5) };
        foreach (int minutes in new[] { 3, 5, 10 }) _interval.Items.Add(new ComboBoxItem { Content = minutes + L.T(" 分钟", " minutes"), Tag = minutes });
        _interval.SelectionChanged += (_, _) => { if (_interval.SelectedValue is int minutes) Save(o => o with { AutomaticIntervalMinutes = minutes }); };
        AutomationProperties.SetName(_interval, L.T("自动理解最少间隔", "Minimum automatic-analysis interval"));
        Grid.SetColumn(_interval, 1); interval.Children.Add(_interval); read.Children.Add(interval);
        Details(read, "ScreenUnderstandingRules", L.T("运行方式", "How it works"), L.T(
            "跟随当前聊天模型；Ollama 需已加载。仅按需读取当前窗口，沿用应用排除、全屏安静和聊天优先规则。输入或切换窗口会取消过期分析。\n\n文字与截图仅用于当次分析，不保存为截图文件、聊天记录或共享记忆；无法安全读取的窗口会跳过。",
            "Uses the selected chat model; Ollama must already be loaded. Reads only the current window when needed, respecting exclusions, full-screen quiet and chat priority. Input or window changes cancel stale analysis.\n\nText and screenshots are used only for the current analysis and are not saved as files, chat history or shared memory. Windows that cannot be read safely are skipped."));

        var memory = Card(stack, L.T("应用分类记忆", "Remembered app categories"));
        _learn = Toggle(memory, "ScreenUnderstandingLearn", L.T("自动记住应用分类", "Remember app categories automatically"), value => Save(o => o with { LearnApplications = value }));
        _memories = new ListBox { Name = "ScreenUnderstandingMemories", Height = 156, Margin = new Thickness(0, 10, 0, 10), FontSize = 13 };
        AutomationProperties.SetName(_memories, L.T("已记住的应用", "Remembered applications")); memory.Children.Add(_memories);
        _memoryDetail = Text("ScreenUnderstandingMemoryDetail", ""); _memoryDetail.Margin = new Thickness(0, 0, 0, 10); memory.Children.Add(_memoryDetail);
        var edit = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
        _category = new ComboBox { Name = "ScreenUnderstandingMemoryCategory", SelectedValuePath = "Tag", Width = 180,
            FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 10, 8) };
        foreach (string key in new[] { "work", "game", "media", "daily" }) _category.Items.Add(new ComboBoxItem { Content = Category(key), Tag = key });
        AutomationProperties.SetName(_category, L.T("纠正应用分类", "Correct app category")); edit.Children.Add(_category);
        _correct = Button("ScreenUnderstandingCorrectMemory", L.T("保存分类", "Save category"), CorrectMemory); edit.Children.Add(_correct);
        _delete = Button("ScreenUnderstandingDeleteMemory", L.T("删除选中", "Delete selected"), DeleteMemory); edit.Children.Add(_delete);
        memory.Children.Add(edit);
        _clear = Button("ScreenUnderstandingClearMemories", L.T("清空全部", "Clear all"), () => Execute(_host.ClearMemories));
        AutomationProperties.SetName(_clear, L.T("清空应用分类记忆", "Clear all app classification memories")); edit.Children.Add(_clear);
        Details(memory, "ScreenUnderstandingMemoryRules", L.T("记忆规则", "Memory rules"), L.T(
            "只保存应用身份、分类、可信度和时间，最多 256 条，90 天后过期；不保存屏幕内容。手动分类优先。删除后可能再次识别，关闭自动记忆可停止新增。",
            "Stores app identity, category, confidence and dates only: up to 256 entries, expiring after 90 days. No screen content is saved. Manual categories take priority. Deleted apps may be learned again; turn off automatic memory to stop new entries."));
        _memories.SelectionChanged += (_, _) => { if (!_sync) SelectionChanged(); };
        _feedback = Text("ScreenUnderstandingFeedback", ""); _feedback.Margin = new Thickness(0, 8, 0, 0); memory.Children.Add(_feedback);
        _host.Changed += HostChanged; Render();
    }

    private void Save(Func<ScreenUnderstandingOptions, ScreenUnderstandingOptions> update)
    {
        if (_sync || _disposed) return;
        Execute(() => _host.UpdateOptions(update(_host.Options).Normalize())); Render();
    }

    private void Execute(Action operation)
    {
        if (_disposed) return;
        try { operation(); _feedback.Text = ""; }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        { _feedback.Text = L.T("操作未完成，请检查本地存储权限后重试。", "The action could not be completed. Check local storage permissions and retry."); }
    }

    private LearnedApplication? Selected => (_memories.SelectedItem as ListBoxItem)?.Tag as LearnedApplication;
    private void CorrectMemory()
    {
        if (Selected is { } selected && _category.SelectedValue is string category)
            Execute(() => _host.UpdateMemoryCategory(selected.Id, category));
    }
    private void DeleteMemory() { if (Selected is { } selected) Execute(() => _host.DeleteMemory(selected.Id)); }

    private void SelectionChanged()
    {
        var selected = Selected;
        _category.IsEnabled = _correct.IsEnabled = _delete.IsEnabled = selected != null;
        _memoryDetail.Visibility = selected == null ? Visibility.Collapsed : Visibility.Visible;
        if (selected == null) { _memoryDetail.Text = ""; return; }
        _category.SelectedValue = selected.Activity;
        _memoryDetail.Text = selected.App + " · " + (selected.UserCorrected ? L.T("你已纠正", "Corrected by you") :
            L.T("自动识别", "Automatically learned") + " · " + selected.Confidence.ToString("P0", CultureInfo.InvariantCulture)) +
            L.T(" · 到期：", " · Expires: ") + selected.ExpiresAt.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private void HostChanged()
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) Render(); else Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) Render(); }));
    }

    private void Render()
    {
        if (_disposed) return;
        _sync = true;
        try
        {
            var options = _host.Options.Normalize();
            _enabled.IsChecked = options.Enabled; _text.IsChecked = options.ReadWindowText;
            _screenshots.IsChecked = options.UseScreenshots; _learn.IsChecked = options.LearnApplications;
            _remote.IsChecked = options.AllowRemoteEndpoint; _interval.SelectedValue = options.AutomaticIntervalMinutes;
            _request.IsEnabled = options.Enabled && (options.ReadWindowText || options.UseScreenshots);
            _status.Text = Limit(_host.Status.Summary, 160); _detail.Text = Limit(_host.Status.Detail, 700);
            _detail.Visibility = string.IsNullOrWhiteSpace(_host.Status.Detail) ? Visibility.Collapsed : Visibility.Visible;
            _lastSummary.Text = string.IsNullOrWhiteSpace(_host.Status.LastSummary)
                ? "" : Limit(_host.Status.LastSummary, 700);
            _lastSummary.Visibility = string.IsNullOrWhiteSpace(_lastSummary.Text) ? Visibility.Collapsed : Visibility.Visible;
            var memories = _host.GetMemories();
            string fingerprint = string.Join("\n", memories.Select(item => item.Id + "|" + item.Activity + "|" + item.ExpiresAt.Ticks + "|" + item.UserCorrected));
            if (_memoryFingerprint != fingerprint)
            {
                string? selectedId = Selected?.Id;
                _memories.Items.Clear();
                foreach (var item in memories)
                {
                    var entry = new ListBoxItem { Tag = item, Content = item.App + " · " + Category(item.Activity) + " · " +
                        (item.UserCorrected ? L.T("已纠正", "Corrected") : L.T("自动识别", "Learned")) };
                    _memories.Items.Add(entry); if (item.Id == selectedId) _memories.SelectedItem = entry;
                }
                _memoryFingerprint = fingerprint;
                SelectionChanged();
            }
            _clear.IsEnabled = memories.Count > 0;
        }
        finally { _sync = false; }
    }

    internal static string Category(string key) => key switch
    {
        "work" => L.T("工作与学习", "Work and study"), "game" => L.T("游戏", "Gaming"),
        "media" => L.T("影音娱乐", "Video and music"), _ => L.T("日常", "Everyday use")
    };
    private static string Limit(string? value, int limit) => value == null ? "" : value.Length > limit ? value[..limit] + "…" : value;
    private static TextBlock Text(string name, string value, bool bold = false)
    { var text = Ui.Text(value, bold ? 15 : 13, bold ? "#21392F" : "#71826D", bold); text.Name = name; text.TextWrapping = TextWrapping.Wrap; text.LineHeight = 21; return text; }
    private static TextBlock Paragraph(string value) { var text = Text("", value); text.Margin = new Thickness(0, 5, 0, 5); return text; }
    private static StackPanel Card(Panel parent, string title)
    {
        var panel = new StackPanel(); panel.Children.Add(Ui.Text(title, 18, "#294A35", true));
        parent.Children.Add(new Border { Child = panel, Background = Ui.Brush("#FFFFFF"), BorderBrush = Ui.Brush("#DCE5DB"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16), Padding = new Thickness(18), Margin = new Thickness(0, 0, 0, 14) });
        return panel;
    }
    private static CheckBox Toggle(Panel panel, string name, string label, Action<bool> changed)
    {
        var text = Ui.Text(label, 13, "#385346"); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 0, 14, 0);
        var box = new CheckBox { Name = name, Content = text, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(box, label); box.Checked += (_, _) => changed(true); box.Unchecked += (_, _) => changed(false);
        panel.Children.Add(box); return box;
    }
    private static void Details(Panel parent, string name, string title, string text)
    {
        var expander = new Expander { Name = name, Header = title, IsExpanded = false, Content = Paragraph(text),
            Margin = new Thickness(0, 10, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(expander, title); parent.Children.Add(expander);
    }
    private static Button Button(string name, string label, Action action)
    { var button = Ui.Button(label, (_, _) => action()); button.Name = name; button.Margin = new Thickness(0, 0, 10, 8); AutomationProperties.SetName(button, label); return button; }
    public void Dispose() { if (_disposed) return; _disposed = true; _host.Changed -= HostChanged; }
}
