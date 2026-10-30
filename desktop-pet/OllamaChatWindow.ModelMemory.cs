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
    internal string? ModelMemoryOperationText { get; private set; }
    internal string? DashboardModelControlBlockedReason => _shutdown ? L.T("聊天服务正在关闭。", "The chat service is closing.") :
        ModelMemoryBusy ? L.T("模型加载或卸载正在进行。", "A model is being loaded or unloaded.") :
        _generating || _userSendPending ? L.T("正在生成聊天回复，请等待回复结束。", "A reply is being generated. Wait for it to finish.") :
        VoiceInputBusy ? L.T("正在录音或识别语音，请先结束语音输入。", "Recording or transcribing audio. Finish voice input first.") :
        _switching || _configuring || _sessionSwitching || _preparingImage ? L.T("聊天窗口正在切换或准备输入，请稍后重试。", "Chat is switching or preparing input. Please retry shortly.") : null;

    private void AddModelMemoryControls(Grid controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(1, 5, 0, 0) };
        var label = Ui.Text(L.T("聊天模型", "Chat model"), 10, "#718267"); label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 8, 0); row.Children.Add(label);
        _modelPreload = Ui.Button(L.T("预加载", "Preload"), async (_, _) => await SetModelLoadedAsync(true));
        _modelPreload.Name = "OllamaPreloadModel";
        _modelPreload.ToolTip = L.T("提前加载当前选中的聊天模型；不改变 Ollama 自动保留时长，不发送对话", "Load the selected chat model in advance without sending a message or changing Ollama's retention setting");
        _modelUnload = Ui.Button(L.T("卸载", "Unload"), async (_, _) => await SetModelLoadedAsync(false));
        _modelUnload.Name = "OllamaUnloadModel";
        _modelUnload.ToolTip = L.T("释放当前聊天模型占用的内存/显存；不会删除模型，下次发送消息时仍可自动加载", "Release the chat model's RAM/GPU memory without deleting it; the next message can load it again");
        foreach (var button in new[] { _modelPreload, _modelUnload })
        {
            button.FontSize = 10; button.Padding = new Thickness(9, 3, 9, 3); button.Margin = new Thickness(0, 0, 6, 0);
            AutomationProperties.SetName(button, button == _modelPreload ? L.T("预加载当前聊天模型", "Preload the current chat model") : L.T("卸载当前聊天模型", "Unload the current chat model")); row.Children.Add(button);
        }
        Grid.SetRow(row, 1); Grid.SetColumnSpan(row, 4); controls.Children.Add(row);
    }

    private bool CanStartModelMemoryOperation => !_shutdown && !_generating && !_userSendPending && !_switching &&
        !_configuring && !_preparingImage && !_sessionSwitching && !VoiceInputBusy && !ModelMemoryBusy;

    private bool CanChangeModelMemory => CanStartModelMemoryOperation && IsVisible &&
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
            SetStatus(L.T("已停止等待模型操作，服务器可能仍在完成加载或释放。", "Stopped waiting. The server may still be loading or releasing the model."));
    }

    internal async Task SetModelLoadedAsync(bool load)
    {
        if (!CanChangeModelMemory) return;
        await ChangeModelMemoryAsync(load, validateSelection: false);
    }

    // The dashboard may operate before the chat window has ever been shown. Use
    // this window's shared gate so an operation cannot race a reply or behavior.
    internal Task<string> SetDashboardModelLoadedAsync(bool load)
    {
        if (!CanStartModelMemoryOperation)
            return Task.FromResult(DashboardModelControlBlockedReason ?? L.T("聊天或模型操作正在进行，请等待当前操作结束后重试。", "A chat or model operation is in progress. Wait for it to finish and retry."));
        return ChangeModelMemoryAsync(load, validateSelection: true);
    }

    private async Task<string> ChangeModelMemoryAsync(bool load, bool validateSelection)
    {
        CancelBehaviorReaction();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        _modelMemoryCancellation = operation;
        long epoch = ++_modelMemoryEpoch;
        var client = _client; string model = _model;
        bool entered = false;
        ModelMemoryOperationText = load ? L.T("正在加载聊天模型…", "Loading chat model…") : L.T("正在卸载聊天模型…", "Unloading chat model…");
        SetStatus(load ? L.T("正在预加载聊天模型，可点击停止取消等待…", "Preloading chat model… Click Stop to stop waiting.") : ModelMemoryOperationText);
        RefreshControls();
        try
        {
            await _modelCallGate.WaitAsync(operation.Token); entered = true;
            if (validateSelection)
            {
                // Check only the chosen model. CheckConnectionAsync is unsuitable
                // here because it may select and persist a different fallback.
                var installed = await client.ListModelsAsync(operation.Token);
                if (!installed.Contains(model, StringComparer.Ordinal))
                    throw new OllamaException(L.T("当前所选模型未安装在此 Ollama 服务器，请在聊天窗口选择已安装的模型后重试。", "The selected model is not installed on this Ollama server. Select an installed model in chat and retry."));
                var capabilities = await client.GetModelInfoAsync(model, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                if (epoch != _modelMemoryEpoch || !ReferenceEquals(client, _client) || model != _model)
                    throw new OperationCanceledException(operation.Token);
                if (capabilities.SupportsChat == false)
                    throw new OllamaException(L.T("当前所选模型不支持聊天，请先在聊天窗口选择其他模型。", "The selected model does not support chat. Choose another model in chat first."));
                SetModelList(installed, model); _capabilities = capabilities; _connected = true;
            }
            if (load) await client.PreloadAsync(model, operation.Token);
            else await client.UnloadAsync(model, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_shutdown || epoch != _modelMemoryEpoch || !ReferenceEquals(client, _client) || model != _model)
                return L.T("模型操作的目标已改变，请刷新状态确认结果。", "The model operation's target changed. Refresh its status to confirm the result.");
            if (load) _lastUsedModel = model;
            else if (_lastUsedModel == model) _lastUsedModel = null;
            string result = load ? L.T("聊天模型已预加载；自动保留时长沿用 Ollama 原有设置。", "Chat model preloaded. Ollama's existing retention setting is unchanged.") : L.T("聊天模型已卸载；下次发送消息会按原有方式自动加载。", "Chat model unloaded. The next message will load it as usual.");
            if (IsVisible) SetStatus(result);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!_shutdown && IsVisible && epoch == _modelMemoryEpoch && ReferenceEquals(client, _client) && model == _model)
                SetStatus(L.T("已停止等待模型操作，服务器可能仍在完成加载或释放。", "Stopped waiting. The server may still be loading or releasing the model."));
            return L.T("已停止等待模型操作，服务器可能仍在完成加载或释放，请刷新状态确认。", "Stopped waiting. The server may still be loading or releasing the model. Refresh to confirm.");
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            if (!_shutdown && IsVisible && epoch == _modelMemoryEpoch) SetStatus(L.T("模型操作未完成：", "Model operation incomplete: ") + ex.Message);
            return L.T("模型操作未完成：", "Model operation incomplete: ") + ex.Message;
        }
        finally
        {
            if (entered) _modelCallGate.Release();
            if (ReferenceEquals(_modelMemoryCancellation, operation))
            { _modelMemoryCancellation = null; ModelMemoryOperationText = null; }
            if (!_shutdown) RefreshControls();
        }
    }
}
