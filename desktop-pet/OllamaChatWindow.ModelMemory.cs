using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace DesktopPet;

internal sealed partial class OllamaChatWindow
{
    private readonly Action<bool>? _voiceChatSessionActive;
    private Button? _modelPreload, _modelUnload;
    private CancellationTokenSource? _modelMemoryCancellation;
    private long _modelMemoryEpoch;
    private bool ModelMemoryBusy => _modelMemoryCancellation != null;

    private void AddModelMemoryControls(Grid controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 5, 0, 0) };
        var label = Ui.Text("聊天模型", 10, "#718267"); label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(label);
        _modelPreload = Ui.Button("预加载", async (_, _) => await SetModelLoadedAsync(true));
        _modelPreload.Name = "OllamaPreloadModel";
        _modelPreload.ToolTip = "提前加载当前选中的聊天模型；不改变 Ollama 自动保留时长，不发送对话";
        _modelUnload = Ui.Button("卸载", async (_, _) => await SetModelLoadedAsync(false));
        _modelUnload.Name = "OllamaUnloadModel";
        _modelUnload.ToolTip = "释放当前聊天模型占用的内存/显存；不会删除模型，下次发送消息时仍可自动加载";
        foreach (var button in new[] { _modelPreload, _modelUnload })
        {
            button.FontSize = 10; button.Padding = new Thickness(9, 3, 9, 3); button.Margin = new Thickness(0, 0, 6, 0);
            AutomationProperties.SetName(button, button == _modelPreload ? "预加载当前聊天模型" : "卸载当前聊天模型"); row.Children.Add(button);
        }
        Grid.SetRow(row, 1); Grid.SetColumnSpan(row, 4); controls.Children.Add(row);
    }

    private bool CanChangeModelMemory => !_shutdown && IsVisible && !_generating && !_userSendPending && !_switching &&
        !_configuring && !_preparingImage && !_sessionSwitching && !VoiceInputBusy && !ModelMemoryBusy &&
        _connected && _installedModels.Contains(_model, StringComparer.Ordinal) && _capabilities.SupportsChat != false;

    private void RefreshModelMemoryControls()
    {
        if (_modelPreload != null) _modelPreload.IsEnabled = CanChangeModelMemory;
        if (_modelUnload != null) _modelUnload.IsEnabled = CanChangeModelMemory;
    }

    private void CancelModelMemoryOperation(bool showStatus = false)
    {
        _modelMemoryEpoch++;
        _modelMemoryCancellation?.Cancel();
        if (showStatus && ModelMemoryBusy && !_shutdown && IsVisible)
            SetStatus("已停止等待模型操作，服务器可能仍在完成加载或释放。");
    }

    internal async Task SetModelLoadedAsync(bool load)
    {
        if (!CanChangeModelMemory) return;
        CancelBehaviorReaction();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        _modelMemoryCancellation = operation;
        long epoch = ++_modelMemoryEpoch;
        var client = _client; string model = _model;
        bool entered = false;
        SetStatus(load ? "正在预加载聊天模型，可点击停止取消等待…" : "正在卸载聊天模型…");
        RefreshControls();
        try
        {
            await _modelCallGate.WaitAsync(operation.Token); entered = true;
            if (load) await client.PreloadAsync(model, operation.Token);
            else await client.UnloadAsync(model, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_shutdown || epoch != _modelMemoryEpoch || !ReferenceEquals(client, _client) || model != _model) return;
            if (load) _lastUsedModel = model;
            else if (_lastUsedModel == model) _lastUsedModel = null;
            if (IsVisible) SetStatus(load ? "聊天模型已预加载；自动保留时长沿用 Ollama 原有设置。" : "聊天模型已卸载；下次发送消息会按原有方式自动加载。");
        }
        catch (OperationCanceledException)
        {
            if (!_shutdown && IsVisible && epoch == _modelMemoryEpoch && ReferenceEquals(client, _client) && model == _model)
                SetStatus("已停止等待模型操作，服务器可能仍在完成加载或释放。");
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            if (!_shutdown && IsVisible && epoch == _modelMemoryEpoch) SetStatus("模型操作未完成：" + ex.Message);
        }
        finally
        {
            if (entered) _modelCallGate.Release();
            if (ReferenceEquals(_modelMemoryCancellation, operation)) _modelMemoryCancellation = null;
            if (!_shutdown) RefreshControls();
        }
    }
}
