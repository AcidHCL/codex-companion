using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopPet;

// Candidate clients stay isolated from the active conversation until the user applies a successful test.
internal sealed class OllamaConnectionWindow : Window
{
    private readonly Func<Uri, IOllamaClient> _clientFactory;
    private readonly Func<IOllamaClient, IReadOnlyList<string>, CancellationToken, Task<bool>> _apply;
    private readonly TextBox _address;
    private readonly TextBlock _status;
    private readonly Button _test, _save;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _operation;
    private IOllamaClient? _candidate;
    private IReadOnlyList<string> _models = Array.Empty<string>();
    private string? _testedInput;
    private bool _busy, _closed;

    public OllamaConnectionWindow(Uri currentEndpoint, Func<Uri, IOllamaClient> clientFactory,
        Func<IOllamaClient, IReadOnlyList<string>, CancellationToken, Task<bool>> apply)
    {
        _clientFactory = clientFactory; _apply = apply;
        Title = AppVersion.WindowTitle(L.T("Ollama 连接设置", "Ollama connection settings"));
        Width = L.IsEnglish ? 500 : 410; Height = L.IsEnglish ? 470 : 410; MinWidth = 320; MinHeight = 330;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        UseLayoutRounding = true; Resources.MergedDictionaries.Add(Ui.CreateResources());
        var body = new StackPanel { Margin = new Thickness(20, 15, 20, 16) };
        var heading = new DockPanel();
        var close = Ui.Button("×", (_, _) => Close()); close.Name = "OllamaCancelConnection"; close.Width = 30; close.Height = 30; close.Padding = new Thickness(0); close.ToolTip = L.T("关闭，不更改连接", "Close without changing the connection");
        AutomationProperties.SetName(close, L.T("关闭连接设置，不保存", "Close connection settings without saving")); DockPanel.SetDock(close, Dock.Right); heading.Children.Add(close);
        var title = Ui.Text(L.T("连接 Ollama", "Connect to Ollama"), 20, "#214332", true); title.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(title); body.Children.Add(heading);
        var description = Ui.Text(L.T("连接此电脑，或你自己的 Ollama 服务器。", "Connect to this computer or your own Ollama server."), 12, "#75866B"); description.Margin = new Thickness(0, 9, 0, 15); description.TextWrapping = TextWrapping.Wrap; body.Children.Add(description);
        body.Children.Add(Ui.Text(L.T("服务器地址与端口", "Server address and port"), 12, "#42643C", true));
        _address = new TextBox { Name = "OllamaAddressInput", Text = currentEndpoint.GetLeftPart(UriPartial.Authority), FontSize = 13, Height = 36, Padding = new Thickness(9, 7, 9, 7), BorderThickness = new Thickness(0), Background = Brushes.Transparent, MaxLength = 2048 };
        AutomationProperties.SetName(_address, L.T("Ollama 服务器地址与端口", "Ollama server address and port"));
        body.Children.Add(new Border { Child = _address, Background = Brushes.White, BorderBrush = Ui.Brush("#D7E2CF"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Margin = new Thickness(0, 7, 0, 0) });
        var examples = Ui.Text(L.T("本机：127.0.0.1:11434\n其他电脑：192.168.1.20:11434", "This computer: 127.0.0.1:11434\nAnother computer: 192.168.1.20:11434"), 11, "#819175"); examples.LineHeight = 18; examples.Margin = new Thickness(1, 7, 0, 11); body.Children.Add(examples);
        var note = Ui.Text(L.T("测试只读取模型列表，不发送对话或图片。\n切换服务器会保存当前对话，并恢复目标服务器的记录；历史与记忆不跨服务器发送。", "Testing only reads the model list; no messages or images are sent.\nSwitching servers saves this conversation and restores the destination server's history. History and memory are not sent across servers."), 11, "#637B58"); note.TextWrapping = TextWrapping.Wrap; note.LineHeight = 18;
        body.Children.Add(new Border { Child = note, Background = Ui.Brush("#EAF1E2"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8) });
        _status = Ui.Text(L.T("先测试连接，成功后再保存应用。", "Test the connection first, then save and apply it."), 11, "#6C8261"); _status.Name = "OllamaConnectionStatus"; _status.TextWrapping = TextWrapping.Wrap; _status.MinHeight = 38; _status.Margin = new Thickness(1, 12, 0, 6); body.Children.Add(_status);
        var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition());
        _test = Ui.Button(L.T("测试连接", "Test connection"), async (_, _) => await TestConnectionAsync()); _test.Name = "OllamaTestConnection"; _test.Margin = new Thickness(0, 0, 5, 0); actions.Children.Add(_test);
        _save = Ui.Button(L.T("保存并应用", "Save and apply"), async (_, _) => await ApplyAsync()); _save.Name = "OllamaApplyConnection"; _save.Background = Ui.Brush("#356446"); _save.Foreground = Brushes.White; _save.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(_save, 1); actions.Children.Add(_save); body.Children.Add(actions);
        var version = Ui.VersionLabel(false); version.Margin = new Thickness(0, 10, 0, 0); version.HorizontalAlignment = HorizontalAlignment.Right; body.Children.Add(version);
        Content = new Border { Background = Ui.Brush("#F7F9F2"), BorderBrush = Ui.Brush("#C9D6C0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled } };
        _address.TextChanged += (_, _) => { InvalidateCandidate(); SetStatus(L.T("地址已更改，请重新测试连接。", "Address changed. Please test the connection again.")); RefreshControls(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _operation?.Cancel(); InvalidateCandidate();
            // In-flight operations own their linked token sources until their continuations finish.
            _lifetime.Dispose();
        };
        Loaded += (_, _) => { _address.Focus(); _address.SelectAll(); };
        RefreshControls();
    }

    private void InvalidateCandidate()
    {
        _candidate?.Dispose(); _candidate = null; _models = Array.Empty<string>(); _testedInput = null;
    }

    private void RefreshControls()
    {
        _address.IsReadOnly = _busy;
        _test.IsEnabled = !_busy && !_closed && !string.IsNullOrWhiteSpace(_address.Text);
        _save.IsEnabled = !_busy && !_closed && _candidate != null && _testedInput == _address.Text;
    }

    private void SetStatus(string text, bool error = false)
    { _status.Text = text; _status.Foreground = Ui.Brush(error ? "#A44F39" : "#6C8261"); }

    internal async Task TestConnectionAsync()
    {
        if (_busy || _closed) return;
        InvalidateCandidate(); _busy = true; RefreshControls(); SetStatus(L.T("正在测试连接并读取已安装模型…", "Testing the connection and reading installed models…"));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation; operation.CancelAfter(TimeSpan.FromSeconds(20));
        IOllamaClient? candidate = null;
        try
        {
            string input = _address.Text;
            Uri endpoint = OllamaEndpoint.Parse(input);
            candidate = _clientFactory(endpoint);
            var models = await candidate.ListModelsAsync(operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed || input != _address.Text) return;
            _candidate = candidate; candidate = null; _models = models; _testedInput = input;
            SetStatus(L.T($"连接成功：{models.Count} 个已安装模型。", $"Connected: {models.Count} installed model(s). ") + (endpoint.IsLoopback ? L.T("聊天将在本机处理。", "Chat will be processed on this computer.") : L.T("聊天与图片将发送到 ", "Messages and images will be sent to ") + endpoint.GetLeftPart(UriPartial.Authority) + L.T("。", ".")));
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus(L.T("连接测试已取消或超时，当前连接保持不变。", "Connection test canceled or timed out. The current connection is unchanged."), true); }
        catch (Exception ex) { if (!_closed) SetStatus(ex is OllamaException || ex is ArgumentException ? ex.Message : L.T("连接失败，请检查 Ollama 是否运行以及地址、端口。当前连接保持不变。", "Connection failed. Check that Ollama is running and the address and port are correct. The current connection is unchanged."), true); }
        finally
        {
            candidate?.Dispose(); if (ReferenceEquals(_operation, operation)) _operation = null;
            _busy = false; if (!_closed) RefreshControls();
        }
    }

    internal async Task ApplyAsync()
    {
        if (_busy || _closed || _candidate == null || _testedInput != _address.Text) return;
        _busy = true;
        var candidate = _candidate; _candidate = null;
        var models = _models;
        RefreshControls(); SetStatus(L.T("正在应用连接设置…", "Applying connection settings…"));
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operation = operation;
        bool adopted = false;
        try
        {
            adopted = await _apply(candidate, models, operation.Token);
            if (adopted && !_closed) Close();
            else if (!_closed) SetStatus(L.T("未更改连接，请重新测试后应用。", "The connection was not changed. Test it again before applying."), true);
        }
        catch (OperationCanceledException) { if (!_closed) SetStatus(L.T("应用已取消或超时，原连接保持不变。请重新测试。", "Apply canceled or timed out. The previous connection is unchanged. Please test again."), true); }
        catch (Exception ex) { if (!_closed) SetStatus((ex is OllamaException ? ex.Message : L.T("未能应用连接设置，原连接保持不变。", "Could not apply connection settings. The previous connection is unchanged.")) + L.T(" 请重新测试后重试。", " Please test the connection again and retry."), true); }
        finally
        {
            if (!adopted) candidate.Dispose();
            if (ReferenceEquals(_operation, operation)) _operation = null;
            _busy = false; if (!_closed) RefreshControls();
        }
    }
}
