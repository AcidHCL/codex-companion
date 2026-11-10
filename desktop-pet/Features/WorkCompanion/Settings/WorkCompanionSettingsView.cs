using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet.WorkCompanion;

// A feature-owned page: all observation/model work stays behind the host.
internal sealed class WorkCompanionSettingsView : UserControl, IDisposable
{
    private readonly IWorkCompanionSettingsHost _host;
    private readonly CheckBox _enabled, _applications, _titles, _changes, _breaks, _codex, _speech;
    private readonly CheckBox _work, _game, _media, _daily, _quietFullscreen;
    private readonly ComboBox _breakAfter, _gameBreakAfter, _mediaBreakAfter, _dailyBreakAfter, _cooldown, _activityChoice;
    private readonly TextBox _excluded, _activityApp;
    private readonly ListBox _activityList;
    private readonly TextBlock _summary, _detail, _reaction, _feedback;
    private readonly TextBlock _activityFeedback;
    private readonly Button _pause, _saveExclusions, _removeActivity;
    private string? _mappingFingerprint;
    private bool _sync, _disposed, _exclusionsDirty;

    internal WorkCompanionSettingsView(IWorkCompanionSettingsHost host, Func<string>? readDiagnostics = null)
    {
        _host = host;
        var stack = new StackPanel { Margin = new Thickness(0, 0, 10, 4) };
        Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var intro = Card(stack, L.T("主动陪伴", "Proactive companion"));
        _enabled = Toggle(intro, "WorkCompanionEnabled", L.T("启用", "Enabled"), value => Save(o => o with { Enabled = value }));
        AutomationProperties.SetName(_enabled, L.T("启用主动陪伴", "Enable proactive companion"));
        var status = intro;
        _summary = NamedText("WorkCompanionStatus", "", 15, true); status.Children.Add(_summary);
        _detail = NamedText("WorkCompanionStatusDetail", ""); _detail.Margin = new Thickness(0, 5, 0, 0); status.Children.Add(_detail);
        _reaction = NamedText("WorkCompanionLastReaction", ""); _reaction.Margin = new Thickness(0, 10, 0, 0); status.Children.Add(_reaction);
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _pause = ActionButton("WorkCompanionPause", L.T("暂停陪伴", "Pause companion"), () => Execute(() => _host.SetPaused(!_host.Status.Paused)));
        actions.Children.Add(_pause);
        actions.Children.Add(ActionButton("WorkCompanionClear", L.T("清除本次观察", "Clear observations"), () => Execute(() => _host.ClearTransientState())));
        status.Children.Add(actions);
        if (readDiagnostics != null)
        {
            var diagnosticRows = NamedText("WorkCompanionDiagnosticRecords", "");
            diagnosticRows.LineHeight = 21;
            void RefreshDiagnostics()
            {
                if (_disposed) return;
                try
                {
                    string records = readDiagnostics();
                    diagnosticRows.Text = string.IsNullOrWhiteSpace(records)
                        ? L.T("暂时没有陪伴记录。", "No companion records yet.") : Limit(records, 16000);
                }
                catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
                { diagnosticRows.Text = L.T("暂时无法读取记录，请稍后刷新。", "Could not read the records. Refresh again shortly."); }
            }
            var diagnosticBody = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            diagnosticBody.Children.Add(new ScrollViewer { Content = diagnosticRows, MaxHeight = 220,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            var refresh = ActionButton("WorkCompanionRefreshDiagnostics", L.T("刷新记录", "Refresh records"), RefreshDiagnostics);
            refresh.HorizontalAlignment = HorizontalAlignment.Left; refresh.Margin = new Thickness(0, 10, 0, 0);
            diagnosticBody.Children.Add(refresh);
            var diagnostics = new Expander { Name = "WorkCompanionDiagnostics", Header = L.T("最近陪伴记录", "Recent companion records"),
                Content = diagnosticBody, IsExpanded = false, Margin = new Thickness(0, 12, 0, 0),
                HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(diagnostics, L.T("最近陪伴记录", "Recent companion records"));
            diagnostics.Expanded += (_, _) => RefreshDiagnostics();
            status.Children.Add(diagnostics);
        }

        var observe = Card(stack, L.T("观察范围", "Observation"));
        _applications = Toggle(observe, "WorkCompanionObserveApplications", L.T("前台应用与使用时长", "Foreground apps and time spent"), value => Save(o => o with { ObserveApplications = value }));
        _titles = Toggle(observe, "WorkCompanionIncludeTitles", L.T("窗口标题", "Window titles"), value => Save(o => o with { IncludeWindowTitles = value }));
        _titles.ToolTip = L.T("标题可能包含私人文字。", "Titles may contain private information.");
        _codex = Toggle(observe, "WorkCompanionUseCodexEvents", L.T("避免重复 Codex 任务通知", "Avoid duplicate Codex task notices"), value => Save(o => o with { UseCodexEvents = value }));
        _codex.ToolTip = L.T("关闭此项不影响看板通知。", "Turning this off does not affect dashboard notifications.");
        observe.Children.Add(Paragraph(L.T("所选信息会发给当前模型服务，窗口标题可能含私人文字；API 可能产生费用。",
            "Selected information goes to the current model service. Titles may contain private text; API usage may incur charges.")));
        Details(observe, "WorkCompanionRules", L.T("运行方式", "How it works"), L.T(
            "跟随当前聊天模型；Ollama 只使用已加载模型，API 按需请求。正文与截图需在屏幕理解页单独开启。\n\n观察与回应只保留在本次运行中，不写入聊天或共享记忆。密码管理器和系统安全窗口始终跳过。浏览器及未知应用默认归为日常，可在下方修改分类。",
            "Uses the selected chat model: Ollama must already be loaded; APIs are called on demand. Enable text or screenshots separately on the Screen understanding page.\n\nObservations and reactions last only for this session and do not enter chat or shared memory. Password managers and secure system windows are skipped. Browsers and unknown apps default to everyday use; override them below."));

        var contexts = Card(stack, L.T("场景与休息提醒", "Contexts and break reminders"));
        _breaks = Toggle(contexts, "WorkCompanionBreakReminders", L.T("提醒休息", "Suggest breaks"), value => Save(o => o with { BreakReminders = value }));
        var headings = new Grid { Margin = new Thickness(0, 10, 0, 3) };
        headings.ColumnDefinitions.Add(new ColumnDefinition()); headings.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        headings.Children.Add(Paragraph(L.T("陪伴场景", "Companion context")));
        var timeHeading = Paragraph(L.T("多久后提醒", "Remind after")); Grid.SetColumn(timeHeading, 1); headings.Children.Add(timeHeading); contexts.Children.Add(headings);
        (_work, _breakAfter) = ContextRow(contexts, "WorkCompanionWorkEnabled", "WorkCompanionBreakAfter", L.T("工作与学习", "Work and study"),
            value => Save(o => o with { WorkEnabled = value }), value => Save(o => o with { BreakAfterMinutes = value }));
        (_game, _gameBreakAfter) = ContextRow(contexts, "WorkCompanionGameEnabled", "WorkCompanionGameBreakAfter", L.T("游戏", "Gaming"),
            value => Save(o => o with { GameEnabled = value }), value => Save(o => o with { GameBreakAfterMinutes = value }));
        (_media, _mediaBreakAfter) = ContextRow(contexts, "WorkCompanionMediaEnabled", "WorkCompanionMediaBreakAfter", L.T("影音娱乐", "Video and music"),
            value => Save(o => o with { MediaEnabled = value }), value => Save(o => o with { MediaBreakAfterMinutes = value }));
        (_daily, _dailyBreakAfter) = ContextRow(contexts, "WorkCompanionDailyEnabled", "WorkCompanionDailyBreakAfter", L.T("日常浏览与交流", "Browsing and communication"),
            value => Save(o => o with { DailyEnabled = value }), value => Save(o => o with { DailyBreakAfterMinutes = value }));

        var react = Card(stack, L.T("反应", "Reactions"));
        _changes = Toggle(react, "WorkCompanionReactToAppChanges", L.T("切换应用或场景时回应", "Respond to app or context changes"), value => Save(o => o with { ReactToAppChanges = value }));
        _quietFullscreen = Toggle(react, "WorkCompanionQuietFullscreen", L.T("全屏时保持安静", "Stay quiet in full screen"), value => Save(o => o with { QuietFullscreen = value }));
        _quietFullscreen.ToolTip = L.T("全屏时不读取标题或生成回应。", "No titles are read or reactions generated in full screen.");
        _speech = Toggle(react, "WorkCompanionSpeak", L.T("语音播报", "Read reactions aloud"), value => Save(o => o with { SpeakReactions = value }));
        _speech.ToolTip = L.T("同时需要开启语音总开关。", "Requires the main speech switch to be on.");
        _cooldown = Duration(react, "WorkCompanionCooldown", L.T("最短反应间隔", "Minimum reaction interval"), new[] { 3, 5, 10 }, value => Save(o => o with { CooldownMinutes = value }));

        var mapping = Card(stack, L.T("应用分类修正", "App context overrides"));
        var mappingRow = new Grid { Margin = new Thickness(0, 8, 0, 10) };
        mappingRow.ColumnDefinitions.Add(new ColumnDefinition()); mappingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(180) });
        mappingRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        mappingRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); mappingRow.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        mappingRow.Children.Add(Paragraph(L.T("应用进程名", "App process name")));
        var categoryLabel = Paragraph(L.T("场景", "Context")); Grid.SetColumn(categoryLabel, 1); mappingRow.Children.Add(categoryLabel);
        _activityApp = new TextBox { Name = "WorkCompanionActivityApp", MaxLength = 84, FontSize = 13, Padding = new Thickness(8), Margin = new Thickness(0, 0, 10, 0) };
        AutomationProperties.SetName(_activityApp, L.T("要分类的应用进程名称", "Application process name to classify"));
        _activityApp.ToolTip = L.T("例如 mygame.exe，不含路径。", "For example, mygame.exe, without a path.");
        _activityChoice = new ComboBox { Name = "WorkCompanionActivityChoice", SelectedValuePath = "Tag", FontSize = 13, Padding = new Thickness(8, 5, 8, 5) };
        foreach (string value in new[] { "work", "game", "media", "daily" })
            _activityChoice.Items.Add(new ComboBoxItem { Tag = value, Content = ActivityLabel(value) });
        _activityChoice.SelectedValue = "game";
        AutomationProperties.SetName(_activityChoice, L.T("应用陪伴场景", "Application companion context"));
        Grid.SetRow(_activityApp, 1); mappingRow.Children.Add(_activityApp); Grid.SetColumn(_activityChoice, 1); Grid.SetRow(_activityChoice, 1); mappingRow.Children.Add(_activityChoice); mapping.Children.Add(mappingRow);
        var saveActivity = ActionButton("WorkCompanionSaveActivity", L.T("保存", "Save"), SaveActivity);
        saveActivity.Margin = new Thickness(10, 0, 0, 0); Grid.SetColumn(saveActivity, 2); Grid.SetRow(saveActivity, 1); mappingRow.Children.Add(saveActivity);
        AutomationProperties.SetName(saveActivity, L.T("保存应用分类", "Save app context"));
        _activityList = new ListBox { Name = "WorkCompanionActivityList", Height = 112, Margin = new Thickness(0, 12, 0, 10), FontSize = 13 };
        AutomationProperties.SetName(_activityList, L.T("已保存的应用分类", "Saved app contexts")); mapping.Children.Add(_activityList);
        _removeActivity = ActionButton("WorkCompanionRemoveActivity", L.T("恢复自动分类", "Restore automatic classification"), RemoveActivity);
        _removeActivity.HorizontalAlignment = HorizontalAlignment.Left; mapping.Children.Add(_removeActivity);
        _activityList.SelectionChanged += (_, _) =>
        {
            _removeActivity.IsEnabled = _activityList.SelectedItem != null;
            if (!_sync && (_activityList.SelectedItem as ListBoxItem)?.Tag is string name &&
                _host.Options.AppActivities.TryGetValue(name, out string? activity))
            { _activityApp.Text = name; _activityChoice.SelectedValue = activity; }
        };
        _activityFeedback = NamedText("WorkCompanionActivityFeedback", ""); _activityFeedback.Margin = new Thickness(0, 8, 0, 0); mapping.Children.Add(_activityFeedback);

        var exclude = Card(stack, L.T("排除应用", "Excluded apps"));
        exclude.Children.Add(Paragraph(L.T("每行一个进程名，例如 chrome 或 code。", "One process name per line, such as chrome or code.")));
        _excluded = new TextBox { Name = "WorkCompanionExcludedApps", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 105, MaxLength = 6000, FontSize = 13,
            Padding = new Thickness(10), Margin = new Thickness(0, 8, 0, 10) };
        AutomationProperties.SetName(_excluded, L.T("排除的应用进程名称", "Excluded application process names"));
        exclude.Children.Add(_excluded);
        _saveExclusions = ActionButton("WorkCompanionSaveExclusions", L.T("保存排除列表", "Save exclusions"), SaveExclusions);
        _saveExclusions.HorizontalAlignment = HorizontalAlignment.Left; exclude.Children.Add(_saveExclusions);
        _excluded.TextChanged += (_, _) => { if (!_sync) { _exclusionsDirty = true; _saveExclusions.IsEnabled = true; } };
        _feedback = NamedText("WorkCompanionSettingsFeedback", ""); _feedback.Margin = new Thickness(0, 10, 0, 0); exclude.Children.Add(_feedback);
        _host.Changed += HostChanged;
        Render();
    }

    private void SaveActivity()
    {
        string? name = WorkCompanionOptions.NormalizeAppName(_activityApp.Text);
        string? activity = WorkCompanionOptions.NormalizeActivity(_activityChoice.SelectedValue as string);
        if (name == null || activity == null)
        { _activityFeedback.Text = L.T("请输入不含路径或通配符的进程名，并选择场景。", "Enter a process name without a path or wildcard, and choose a context."); return; }
        var mappings = new Dictionary<string, string>(_host.Options.AppActivities, StringComparer.OrdinalIgnoreCase);
        if (mappings.Count >= 64 && !mappings.ContainsKey(name))
        { _activityFeedback.Text = L.T("最多保存 64 个应用分类，请先移除不需要的项目。", "Up to 64 overrides are supported. Remove an unused entry first."); return; }
        mappings[name] = activity;
        try
        {
            _host.UpdateOptions((_host.Options with { AppActivities = mappings }).Normalize());
            _activityFeedback.Text = L.T("应用分类已保存。", "App context saved.");
        }
        catch (Exception) { _activityFeedback.Text = L.T("应用分类未能保存，请稍后重试。", "Could not save the app context. Please try again."); }
        Render();
    }

    private void RemoveActivity()
    {
        if ((_activityList.SelectedItem as ListBoxItem)?.Tag is not string name) return;
        var mappings = new Dictionary<string, string>(_host.Options.AppActivities, StringComparer.OrdinalIgnoreCase);
        mappings.Remove(name);
        try
        {
            _host.UpdateOptions((_host.Options with { AppActivities = mappings }).Normalize());
            _activityFeedback.Text = L.T("已恢复此应用的自动分类。", "Automatic context detection restored for this app.");
        }
        catch (Exception) { _activityFeedback.Text = L.T("分类未能移除，请稍后重试。", "Could not remove the override. Please try again."); }
        Render();
    }

    private void Save(Func<WorkCompanionOptions, WorkCompanionOptions> update)
    {
        if (_sync || _disposed) return;
        Execute(() => _host.UpdateOptions(update(_host.Options).Normalize()));
    }

    private void Execute(Action action)
    {
        if (_disposed) return;
        try { action(); _feedback.Text = ""; }
        catch (Exception) { _feedback.Text = L.T("设置未能保存，请稍后重试。", "Could not save this change. Please try again."); }
        Render();
    }

    private void SaveExclusions()
    {
        if (!WorkCompanionOptions.TryParseExcludedApps(_excluded.Text, out string[] names))
        {
            _feedback.Text = L.T("请填写有效进程名称（不含路径或通配符），最多 64 个，每个最多 80 字。",
                "Use valid process names without paths or wildcards: up to 64 names, 80 characters each.");
            return;
        }
        try
        {
            _host.UpdateOptions((_host.Options with { ExcludedApps = names }).Normalize());
            _exclusionsDirty = false;
            _feedback.Text = L.T("排除列表已保存。", "Exclusions saved.");
        }
        catch (Exception) { _feedback.Text = L.T("排除列表未能保存，请稍后重试。", "Could not save exclusions. Please try again."); }
        Render();
    }

    private void HostChanged()
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        if (Dispatcher.CheckAccess()) Render();
        else Dispatcher.BeginInvoke(new Action(() => { if (!_disposed) Render(); }));
    }

    private void Render()
    {
        if (_disposed) return;
        _sync = true;
        try
        {
            var options = _host.Options.Normalize();
            var status = _host.Status;
            _enabled.IsChecked = options.Enabled;
            _applications.IsChecked = options.ObserveApplications;
            _titles.IsChecked = options.IncludeWindowTitles; _titles.IsEnabled = options.ObserveApplications;
            _work.IsChecked = options.WorkEnabled; _game.IsChecked = options.GameEnabled;
            _media.IsChecked = options.MediaEnabled; _daily.IsChecked = options.DailyEnabled;
            _quietFullscreen.IsChecked = options.QuietFullscreen;
            _changes.IsChecked = options.ReactToAppChanges; _changes.IsEnabled = options.ObserveApplications;
            _breaks.IsChecked = options.BreakReminders; _breaks.IsEnabled = options.ObserveApplications;
            _breakAfter.SelectedValue = options.BreakAfterMinutes; _breakAfter.IsEnabled = options.ObserveApplications && options.BreakReminders && options.WorkEnabled;
            _gameBreakAfter.SelectedValue = options.GameBreakAfterMinutes; _gameBreakAfter.IsEnabled = options.ObserveApplications && options.BreakReminders && options.GameEnabled;
            _mediaBreakAfter.SelectedValue = options.MediaBreakAfterMinutes; _mediaBreakAfter.IsEnabled = options.ObserveApplications && options.BreakReminders && options.MediaEnabled;
            _dailyBreakAfter.SelectedValue = options.DailyBreakAfterMinutes; _dailyBreakAfter.IsEnabled = options.ObserveApplications && options.BreakReminders && options.DailyEnabled;
            _cooldown.SelectedValue = options.CooldownMinutes;
            _codex.IsChecked = options.UseCodexEvents; _speech.IsChecked = options.SpeakReactions;
            string fingerprint = string.Join("\n", options.AppActivities.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => pair.Key + "=" + pair.Value));
            if (_mappingFingerprint != fingerprint)
            {
                string? selected = (_activityList.SelectedItem as ListBoxItem)?.Tag as string;
                _activityList.Items.Clear();
                foreach (var pair in options.AppActivities.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
                {
                    var item = new ListBoxItem { Tag = pair.Key, Content = pair.Key + " · " + ActivityLabel(pair.Value) };
                    _activityList.Items.Add(item);
                    if (StringComparer.OrdinalIgnoreCase.Equals(pair.Key, selected)) _activityList.SelectedItem = item;
                }
                _mappingFingerprint = fingerprint;
            }
            _removeActivity.IsEnabled = _activityList.SelectedItem != null;
            if (!_exclusionsDirty) _excluded.Text = string.Join(Environment.NewLine, options.ExcludedApps);
            _saveExclusions.IsEnabled = _exclusionsDirty;
            _pause.IsEnabled = options.Enabled;
            _pause.Content = status.Paused ? L.T("恢复陪伴", "Resume companion") : L.T("暂停陪伴", "Pause companion");
            AutomationProperties.SetName(_pause, (string)_pause.Content);
            _summary.Text = Limit(status.Summary, 160);
            _detail.Text = Limit(status.Detail, 500);
            _detail.Visibility = string.IsNullOrWhiteSpace(status.Detail) ? Visibility.Collapsed : Visibility.Visible;
            _reaction.Text = string.IsNullOrWhiteSpace(status.LastReaction)
                ? "" : Limit(status.LastReaction, 500);
            _reaction.Visibility = string.IsNullOrWhiteSpace(_reaction.Text) ? Visibility.Collapsed : Visibility.Visible;
        }
        finally { _sync = false; }
    }

    private static string ActivityLabel(string value) => value switch
    {
        "work" => L.T("工作与学习", "Work and study"), "game" => L.T("游戏", "Gaming"),
        "media" => L.T("影音娱乐", "Video and music"), _ => L.T("日常", "Everyday use")
    };

    private static string Limit(string? value, int max) => value == null ? "" : value.Length > max ? value[..max] + "…" : value;
    private static TextBlock NamedText(string name, string text, double size = 13, bool bold = false)
    {
        var label = Ui.Text(text, size, bold ? "#21392F" : "#71826D", bold); label.Name = name;
        label.TextWrapping = TextWrapping.Wrap; return label;
    }
    private static TextBlock Paragraph(string text)
    { var label = NamedText("", text); label.Margin = new Thickness(0, 5, 0, 4); label.LineHeight = 21; return label; }
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
        AutomationProperties.SetName(box, label);
        box.Checked += (_, _) => changed(true); box.Unchecked += (_, _) => changed(false); panel.Children.Add(box); return box;
    }
    private static (CheckBox Enabled, ComboBox After) ContextRow(Panel panel, string enabledName, string afterName,
        string label, Action<bool> enabledChanged, Action<int> afterChanged)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        var enabled = Toggle(row, enabledName, label, enabledChanged); enabled.Margin = new Thickness(0, 5, 22, 5);
        var after = new ComboBox { Name = afterName, SelectedValuePath = "Tag", FontSize = 13, Padding = new Thickness(8, 5, 8, 5) };
        foreach (int value in new[] { 30, 45, 60, 90 }) after.Items.Add(new ComboBoxItem { Content = value + L.T(" 分钟", " minutes"), Tag = value });
        after.SelectionChanged += (_, _) => { if (after.SelectedValue is int value) afterChanged(value); };
        AutomationProperties.SetName(after, label + L.T("休息提醒间隔", " break reminder interval"));
        Grid.SetColumn(after, 1); row.Children.Add(after); panel.Children.Add(row);
        return (enabled, after);
    }
    private static void Details(Panel parent, string name, string title, string text)
    {
        var expander = new Expander { Name = name, Header = title, IsExpanded = false, Content = Paragraph(text),
            Margin = new Thickness(0, 10, 0, 0), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(expander, title); parent.Children.Add(expander);
    }
    private static ComboBox Duration(Panel panel, string name, string label, int[] minutes, Action<int> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 7, 0, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
        var caption = Paragraph(label); caption.VerticalAlignment = VerticalAlignment.Center; row.Children.Add(caption);
        var combo = new ComboBox { Name = name, SelectedValuePath = "Tag", FontSize = 13, Padding = new Thickness(8, 5, 8, 5) };
        foreach (int value in minutes) combo.Items.Add(new ComboBoxItem { Content = value + L.T(" 分钟", " minutes"), Tag = value });
        combo.SelectionChanged += (_, _) => { if (combo.SelectedValue is int value) changed(value); };
        AutomationProperties.SetName(combo, label); Grid.SetColumn(combo, 1); row.Children.Add(combo); panel.Children.Add(row); return combo;
    }
    private static Button ActionButton(string name, string label, Action action)
    {
        var button = Ui.Button(label, (_, _) => action()); button.Name = name; button.Margin = new Thickness(0, 0, 10, 0);
        AutomationProperties.SetName(button, label); return button;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _host.Changed -= HostChanged;
    }
}
