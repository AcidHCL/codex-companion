using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DesktopPet;

internal sealed partial class OllamaChatWindow : Window
{
    private const int MaxHistoryTurns = 12;
    private const int HistoryPageSize = 50;
    private const int MaxInputCharacters = 3000;
    private const int MaxHistoryImages = 4;
    private const int MaxBehaviorReceiptCharacters = 480;
    private readonly DesktopSettings _settings;
    private readonly Func<string> _characterName;
    private readonly Action<bool> _setBusy;
    private readonly Action<string> _say;
    private readonly Action<string, string>? _answerCompleted;
    private readonly Action? _stopSpeech;
    private readonly Action _saveSettings;
    private IOllamaClient _client;
    private readonly IPetWebClient _webClient;
    private readonly Func<Uri, IOllamaClient> _clientFactory;
    private readonly IChatMemoryStore _chatStore;
    private readonly IPetBehaviorHost? _behaviorHost;
    private readonly SemaphoreSlim _modelCallGate = new(1, 1);
    private CancellationTokenSource? _behaviorCancellation;
    private Task<string?>? _behaviorTask;
    private long _behaviorEpoch;
    private bool _behaviorSetting, _lastBehaviorAvailability, _lastBehaviorCommandsAvailability, _userSendPending;
    private DateTimeOffset _lastInputActivity = DateTimeOffset.MinValue;
    private readonly ComboBox _models;
    private readonly TextBox _input;
    private readonly StackPanel _messages;
    private readonly WrapPanel _attachments;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status;
    private readonly TextBlock _contextNote;
    private readonly TextBlock _inputHint;
    private readonly TextBlock _placeholder, _attachmentHint;
    private readonly Border _panel;
    private readonly Grid _layout;
    private readonly Button _send, _stop, _attach, _paste, _newChat, _refresh, _connection;
    private readonly Button _library, _memory, _loadOlder;
    private readonly TextBlock _conversationTitle;
    private readonly CheckBox _webEnabled;
    private readonly List<ChatTurn> _history = new();
    private readonly List<OllamaChatImage> _pendingImages = new();
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _generationCancellation;
    private CancellationTokenSource? _configurationCancellation;
    private int _configurationRevision;
    private Task? _generationTask;
    private string _model;
    private string? _lastUsedModel;
    private IReadOnlyList<string> _installedModels = Array.Empty<string>();
    private OllamaModelCapabilities _capabilities = new(null, null, null);
    private OllamaConnectionWindow? _connectionWindow;
    private bool _connected, _configuring;
    private bool _generating, _switching, _preparingImage, _shutdown, _selectionSync, _imeComposing, _compactLayout;
    private readonly object _streamLock = new();
    private readonly StringBuilder _streamText = new();
    private TextBox? _streamView;
    private readonly DispatcherTimer _streamTimer;
    private readonly DispatcherTimer _draftTimer;
    private ChatConversation? _conversation;
    private string? _storageError;
    private bool _sessionSwitching, _restoringConversation;
    private Task? _sessionTransition;
    private string _inflightDraft = "";
    private OllamaChatImage[] _inflightImages = Array.Empty<OllamaChatImage>();
    private Window? _libraryWindow, _memoryWindow;
    private string _personaKey;
    private TextBlock? _welcome;

    private sealed record ChatTurn(OllamaMessage User, OllamaMessage Assistant, FrameworkElement UserView, FrameworkElement AssistantView, string PersonaKey, long Sequence);

    public OllamaChatWindow(DesktopSettings settings, Func<string> characterName, Action<bool> setBusy,
        Action<string> say, Action saveSettings, IOllamaClient? client = null, Func<Uri, IOllamaClient>? clientFactory = null, IPetWebClient? webClient = null, IChatMemoryStore? chatStore = null, IPetBehaviorHost? behaviorHost = null,
        Action<string, string>? answerCompleted = null, Action? stopSpeech = null,
        PetVoiceInputSession? voiceInputSession = null, Action<bool>? voiceInputActive = null,
        Action<bool>? voiceChatSessionActive = null, PetChatSpeechCallbacks? streamingSpeech = null)
    {
        _settings = settings; _characterName = characterName; _setBusy = setBusy; _say = say; _saveSettings = saveSettings;
        _answerCompleted = answerCompleted; _stopSpeech = stopSpeech;
        _voiceInputActive = voiceInputActive;
        _voiceChatSessionActive = voiceChatSessionActive;
        _streamingSpeech = streamingSpeech;
        _clientFactory = clientFactory ?? (endpoint => new OllamaClient(endpoint));
        _client = client ?? _clientFactory(OllamaEndpoint.Parse(settings.OllamaAddress));
        _webClient = webClient ?? new PetWebClient();
        _chatStore = chatStore ?? new ChatMemoryStore(":memory:");
        _behaviorHost = behaviorHost; _behaviorSetting = settings.LlmBehaviorEnabled;
        _draftTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(450) };
        _draftTimer.Tick += (_, _) => { _draftTimer.Stop(); SaveCurrentDraft(); };
        _model = OllamaModels.Normalize(settings.OllamaModel);
        _personaKey = settings.SkinId.ToLowerInvariant();
        Title = AppVersion.WindowTitle("和桌宠聊天");
        Width = 410; Height = 550; MinWidth = 320; MinHeight = 360;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; WindowStartupLocation = WindowStartupLocation.Manual;
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        Resources.MergedDictionaries.Add(Ui.CreateResources());
        Resources.MergedDictionaries.Add(CreateChatResources());
        AllowDrop = true;
        var root = _layout = new Grid { Margin = new Thickness(14, 12, 14, 12) };
        for (var i = 0; i < 7; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 3 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
        var header = new DockPanel { LastChildFill = true, Height = 32 };
        var close = IconButton("M 4,4 L 12,12 M 12,4 L 4,12", "收起聊天（Esc）", (_, _) => Close());
        close.Name = "OllamaCloseChat"; close.Background = Brushes.Transparent; close.BorderThickness = new Thickness(0); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        var version = Ui.VersionLabel(false, "#83917D", 10); version.VerticalAlignment = VerticalAlignment.Center; version.Margin = new Thickness(6, 0, 9, 0); DockPanel.SetDock(version, Dock.Right); header.Children.Add(version);
        var heading = Ui.Text("和桌宠聊一会儿", 18, "#214332", true); heading.VerticalAlignment = VerticalAlignment.Center; header.Children.Add(heading);
        root.Children.Add(header);

        var controls = new Grid { Margin = new Thickness(0, 9, 0, 9) };
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var modelLabel = new FrameworkElementFactory(typeof(TextBlock));
        modelLabel.SetBinding(TextBlock.TextProperty, new Binding("DisplayName"));
        modelLabel.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        _models = new ComboBox { Name = "OllamaModelSelector", Style = (Style)Resources["OllamaModelComboStyle"], ItemsSource = Array.Empty<OllamaModelChoice>(), ItemTemplate = new DataTemplate { VisualTree = modelLabel }, SelectedValuePath = "Id", FontSize = 11, Height = 32, Padding = new Thickness(7, 3, 5, 3), VerticalContentAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0), Background = Brushes.White, BorderBrush = Ui.Brush("#DCE5D5"), ToolTip = "选择此 Ollama 服务器已安装的模型" };
        TextSearch.SetTextPath(_models, "DisplayName");
        AutomationProperties.SetName(_models, "已安装的聊天模型");
        controls.Children.Add(_models);
        _refresh = IconButton("M 12.5,5 A 5,5 0 1 0 13,10 M 12.5,1.5 L 12.5,5.5 L 8.5,5.5", "检查 Ollama 连接", async (_, _) => await CheckConnectionAsync()); _refresh.Name = "OllamaCheckConnection"; Grid.SetColumn(_refresh, 1); controls.Children.Add(_refresh);
        _newChat = IconButton("M 8,2.5 L 8,13.5 M 2.5,8 L 13.5,8", "开始新对话", async (_, _) => await NewConversationAsync()); _newChat.Name = "OllamaNewConversation"; _newChat.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(_newChat, 2); controls.Children.Add(_newChat);
        _connection = IconButton("M 2,4 L 14,4 M 5,2 L 5,6 M 2,8 L 14,8 M 11,6 L 11,10 M 2,12 L 14,12 M 7,10 L 7,14", "Ollama 地址与端口设置", (_, _) => OpenConnectionSettings()); _connection.Name = "OllamaConnectionSettings"; _connection.Margin = new Thickness(5, 0, 0, 0); Grid.SetColumn(_connection, 3); controls.Children.Add(_connection);
        AddModelMemoryControls(controls);
        Grid.SetRow(controls, 1); root.Children.Add(controls);

        var conversationBar = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        _memory = Ui.Button("记忆", (_, _) => OpenMemory()); _memory.Name = "OllamaSharedMemory";
        _memory.FontSize = 11; _memory.Padding = new Thickness(8, 3, 8, 3); _memory.Margin = new Thickness(5, 0, 0, 0);
        _memory.ToolTip = "管理此 Ollama 服务器所有对话可共享的记忆"; DockPanel.SetDock(_memory, Dock.Right); conversationBar.Children.Add(_memory);
        _library = Ui.Button("历史", async (_, _) => await OpenLibraryAsync()); _library.Name = "OllamaConversationHistory";
        _library.FontSize = 11; _library.Padding = new Thickness(8, 3, 8, 3); DockPanel.SetDock(_library, Dock.Right); conversationBar.Children.Add(_library);
        _conversationTitle = Ui.Text("新对话", 11, "#607456", true); _conversationTitle.Name = "OllamaConversationTitle";
        _conversationTitle.VerticalAlignment = VerticalAlignment.Center; _conversationTitle.TextTrimming = TextTrimming.CharacterEllipsis;
        _conversationTitle.Margin = new Thickness(2, 0, 7, 0); conversationBar.Children.Add(_conversationTitle);
        Grid.SetRow(conversationBar, 2); root.Children.Add(conversationBar);

        _messages = new StackPanel { Margin = new Thickness(10, 10, 10, 8) };
        _scroll = new ScrollViewer { Name = "OllamaMessages", Content = _messages, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var stage = new Border { Background = Ui.Brush("#FFFFFF"), BorderBrush = Ui.Brush("#E0E7DA"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(0, 2, 0, 2), Child = _scroll, ClipToBounds = true };
        Grid.SetRow(stage, 3); root.Children.Add(stage);
        _loadOlder = Ui.Button("加载更早的记录", (_, _) => LoadOlderMessages()); _loadOlder.Name = "OllamaLoadOlderMessages";
        _loadOlder.FontSize = 11; _loadOlder.Padding = new Thickness(8, 5, 8, 5); _loadOlder.Visibility = Visibility.Collapsed;
        AddWelcome();
        _contextNote = Ui.Text(ConnectionNote(), 10, "#84907B"); _contextNote.TextTrimming = TextTrimming.CharacterEllipsis; _contextNote.Margin = new Thickness(1, 5, 0, 5); _contextNote.SetBinding(ToolTipProperty, new Binding("Text") { Source = _contextNote }); Grid.SetRow(_contextNote, 4); root.Children.Add(_contextNote);

        var composer = new StackPanel();
        _attachments = new WrapPanel { Name = "OllamaImagePreviews", Margin = new Thickness(0, 0, 0, 0) }; composer.Children.Add(_attachments);
        _input = new TextBox { Name = "OllamaChatInput", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 64, MinHeight = 44, FontSize = 13, Padding = new Thickness(9, 7, 9, 7), BorderThickness = new Thickness(0), Background = Brushes.Transparent, Foreground = Ui.Brush("#294635"), MaxLength = MaxInputCharacters, ToolTip = "说点什么，或把图片拖到这里。Enter 发送，Shift+Enter 换行；Ctrl+V 粘贴图片。" };
        AutomationProperties.SetName(_input, "聊天输入；支持拖入和粘贴图片");
        _input.PreviewKeyDown += InputKeyDown;
        TextCompositionManager.AddPreviewTextInputStartHandler(_input, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputUpdateHandler(_input, (_, _) => _imeComposing = true);
        TextCompositionManager.AddPreviewTextInputHandler(_input, (_, _) => _imeComposing = false);
        _input.LostKeyboardFocus += (_, _) => _imeComposing = false;
        DataObject.AddPastingHandler(_input, OnPasting);
        _input.TextChanged += (_, _) =>
        {
            if (!_restoringConversation && !_generating) { _lastInputActivity = DateTimeOffset.UtcNow; CancelBehaviorReaction(); }
            RefreshControls(); QueueDraftSave();
        };
        _input.GotKeyboardFocus += (_, _) => CancelBehaviorReaction();
        var inputSurface = new Grid(); inputSurface.Children.Add(_input);
        var placeholder = _placeholder = Ui.Text("说点什么，或拖入图片…", 12, "#94A08D"); placeholder.Margin = new Thickness(10, 8, 10, 0); placeholder.IsHitTestVisible = false;
        inputSurface.Children.Add(placeholder);
        _input.TextChanged += (_, _) => placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        composer.Children.Add(new Border { Background = Brushes.White, BorderBrush = Ui.Brush("#D9E2D2"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11), Child = inputSurface });
        var actions = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 6, 0, 0) };
        _send = IconButton("M 8,13 L 8,3 M 3.5,7.5 L 8,3 L 12.5,7.5", "发送消息（Enter）", async (_, _) => await SendAsync(), true); _send.Name = "OllamaSend"; DockPanel.SetDock(_send, Dock.Right); actions.Children.Add(_send);
        _stop = IconButton("M 4,4 L 12,4 L 12,12 L 4,12 Z", "停止生成或模型加载", (_, _) => { _stopSpeech?.Invoke(); _generationCancellation?.Cancel(); CancelModelMemoryOperation(true); }); _stop.Name = "OllamaStop"; _stop.Margin = new Thickness(0, 0, 6, 0); DockPanel.SetDock(_stop, Dock.Right); actions.Children.Add(_stop);
        _attach = IconButton("M 2.5,3 L 13.5,3 L 13.5,13 L 2.5,13 Z M 3,10 L 6,7 L 9,10 L 11,8 L 13,10 M 10,5.5 L 10.1,5.5", "添加图片（最多 2 张）", async (_, _) => await PickImagesAsync()); _attach.Name = "OllamaAttachImage"; actions.Children.Add(_attach);
        _paste = IconButton("M 5,4 L 3,4 L 3,13.5 L 13,13.5 L 13,4 L 11,4 M 5.5,2.5 L 10.5,2.5 L 10.5,5 L 5.5,5 Z M 6,8 L 10,8 M 6,10.5 L 10,10.5", "粘贴剪贴板图片", async (_, _) => await PasteImageAsync()); _paste.Name = "OllamaPasteImage"; _paste.Margin = new Thickness(5, 0, 0, 0); actions.Children.Add(_paste);
        AddVoiceInputButton(actions);
        _webEnabled = new CheckBox { Name = "OllamaWebEnabled", Content = "联网", IsChecked = settings.WebSearchEnabled, FontSize = 11, Foreground = Ui.Brush("#52734E"), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "按需搜索公开网页、读取消息中的链接。搜索关键词会发往搜索服务，网页内容会交给所选模型；关闭后不发起网页请求。" };
        AutomationProperties.SetName(_webEnabled, "允许联网搜索和读取网页"); actions.Children.Add(_webEnabled);
        var actionHint = _attachmentHint = Ui.Text("", 10, "#87937E"); actionHint.Visibility = Visibility.Collapsed;
        composer.Children.Add(actions);
        AddVoiceInputStatus(composer);
        _inputHint = Ui.Text("Enter 发送 · Shift+Enter 换行", 10, "#87937E"); _inputHint.Margin = new Thickness(1, 4, 0, 0); composer.Children.Add(_inputHint);
        Grid.SetRow(composer, 5); root.Children.Add(composer);
        _status = Ui.Text("正在检查 Ollama 连接…", 10, "#718267"); _status.Name = "OllamaChatStatus"; _status.TextWrapping = TextWrapping.Wrap; _status.TextTrimming = TextTrimming.CharacterEllipsis; _status.MaxHeight = 29; _status.Margin = new Thickness(1, 7, 0, 0); _status.SetBinding(ToolTipProperty, new Binding("Text") { Source = _status }); Grid.SetRow(_status, 6); root.Children.Add(_status);
        _panel = new Border { Name = "OllamaChatPanel", Background = Ui.Brush("#F7F9F2"), BorderBrush = Ui.Brush("#C9D6C0"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(20), Child = root };
        Content = _panel;
        _streamTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(40) };
        _streamTimer.Tick += (_, _) => FlushStream();
        _models.SelectionChanged += async (_, _) => await ChangeModelAsync();
        _webEnabled.Checked += (_, _) => ChangeWebSetting();
        _webEnabled.Unchecked += (_, _) => ChangeWebSetting();
        PreviewDragOver += OnDragOver;
        PreviewDrop += OnDrop;
        PreviewKeyDown += WindowPreviewKeyDown;
        Closing += OnClosing;
        Closed += (_, _) => { Cleanup(); _lifetime.Dispose(); };
        Loaded += async (_, _) => { FocusComposer(); await CheckConnectionAsync(); };
        IsVisibleChanged += (_, _) =>
        {
            _voiceChatSessionActive?.Invoke(IsVisible && !_shutdown);
            if (!IsVisible && !_shutdown) { CancelModelMemoryOperation(); CancelVoiceInput(); CancelConfiguration(); _connectionWindow?.Close(); _libraryWindow?.Close(); _memoryWindow?.Close(); SaveCurrentDraft(); }
            if (!_shutdown) RefreshControls();
        };
        SizeChanged += (_, _) => AdaptCompactLayout();
        InitializeVoiceInput(voiceInputSession);
        try { RestoreConversation(GetScopeConversation(_client.Endpoint, _personaKey)); }
        catch (Exception ex) { ReportStorageFailure(ex); }
        RefreshControls();
    }

    public void FocusComposer()
    {
        if (_shutdown) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (_shutdown || !IsVisible) return;
            _input.Focus(); Keyboard.Focus(_input); _input.CaretIndex = _input.Text.Length;
            if (!_connected && !_switching && !_configuring) _ = CheckConnectionAsync();
        }));
    }

    private static Button IconButton(string geometry, string label, RoutedEventHandler click, bool accent = false)
    {
        var button = new Button
        {
            Width = 32, Height = 32, Padding = new Thickness(7), ToolTip = label,
            Background = accent ? Ui.Brush("#356446") : Ui.Brush("#FFFFFF"),
            BorderBrush = accent ? Ui.Brush("#356446") : Ui.Brush("#DCE5D5"),
            Content = new ShapePath { Data = Geometry.Parse(geometry), Stroke = accent ? Brushes.White : Ui.Brush("#526D4F"), StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round, Width = 16, Height = 16, Stretch = Stretch.None }
        };
        AutomationProperties.SetName(button, label); button.Click += click; return button;
    }

    private static ResourceDictionary CreateChatResources()
    {
        const string xaml = """
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Style x:Key="OllamaModelItemStyle" TargetType="ComboBoxItem">
    <Setter Property="Foreground" Value="#35543D"/>
    <Setter Property="Background" Value="Transparent"/>
    <Setter Property="Padding" Value="9,8"/>
    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBoxItem">
          <Border x:Name="ItemChrome" CornerRadius="7" Background="{TemplateBinding Background}" Padding="{TemplateBinding Padding}" Margin="0,1">
            <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}" VerticalAlignment="Center"/>
          </Border>
          <ControlTemplate.Triggers>
            <Trigger Property="IsSelected" Value="True"><Setter TargetName="ItemChrome" Property="Background" Value="#DFECD7"/></Trigger>
            <Trigger Property="IsHighlighted" Value="True"><Setter TargetName="ItemChrome" Property="Background" Value="#EAF2E5"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="ItemChrome" Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
  <Style x:Key="OllamaModelComboStyle" TargetType="ComboBox">
    <Setter Property="Foreground" Value="#35543D"/>
    <Setter Property="Background" Value="White"/>
    <Setter Property="BorderBrush" Value="#DCE5D5"/>
    <Setter Property="BorderThickness" Value="1"/>
    <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
    <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled"/>
    <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto"/>
    <Setter Property="ItemContainerStyle" Value="{StaticResource OllamaModelItemStyle}"/>
    <Setter Property="Template">
      <Setter.Value>
        <ControlTemplate TargetType="ComboBox">
          <Grid x:Name="ModelChrome" SnapsToDevicePixels="True">
            <ToggleButton x:Name="ModelToggle" Focusable="False" IsTabStop="False" ClickMode="Press"
                          Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}"
                          IsChecked="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <ToggleButton.Template>
                <ControlTemplate TargetType="ToggleButton">
                  <Border Background="{TemplateBinding Background}" BorderBrush="{TemplateBinding BorderBrush}" BorderThickness="{TemplateBinding BorderThickness}" CornerRadius="10">
                    <Path Data="M 0,0 L 4,4 L 8,0" Width="8" Height="4" Stroke="#6C8762" StrokeThickness="1.5" StrokeStartLineCap="Round" StrokeEndLineCap="Round" StrokeLineJoin="Round" HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,11,0"/>
                  </Border>
                </ControlTemplate>
              </ToggleButton.Template>
            </ToggleButton>
            <ContentPresenter Margin="10,0,29,0" VerticalAlignment="Center" HorizontalAlignment="Stretch" IsHitTestVisible="False"
                              Content="{TemplateBinding SelectionBoxItem}" ContentTemplate="{TemplateBinding SelectionBoxItemTemplate}" ContentTemplateSelector="{TemplateBinding ItemTemplateSelector}"/>
            <Popup x:Name="PART_Popup" Placement="Bottom" VerticalOffset="5" AllowsTransparency="True" Focusable="False" StaysOpen="False"
                   IsOpen="{Binding IsDropDownOpen, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}">
              <Border Width="{TemplateBinding ActualWidth}" MaxHeight="200" Background="#FEFFFC" BorderBrush="#CFDEC5" BorderThickness="1" CornerRadius="11" Padding="4">
                <ScrollViewer CanContentScroll="True" HorizontalScrollBarVisibility="Disabled" VerticalScrollBarVisibility="Auto">
                  <ItemsPresenter KeyboardNavigation.DirectionalNavigation="Contained"/>
                </ScrollViewer>
              </Border>
            </Popup>
          </Grid>
          <ControlTemplate.Triggers>
            <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="ModelToggle" Property="Background" Value="#F4F8EF"/></Trigger>
            <Trigger Property="IsKeyboardFocusWithin" Value="True"><Setter TargetName="ModelToggle" Property="BorderBrush" Value="#779B68"/></Trigger>
            <Trigger Property="IsDropDownOpen" Value="True"><Setter TargetName="ModelToggle" Property="BorderBrush" Value="#779B68"/></Trigger>
            <Trigger Property="IsEnabled" Value="False"><Setter TargetName="ModelChrome" Property="Opacity" Value="0.45"/></Trigger>
          </ControlTemplate.Triggers>
        </ControlTemplate>
      </Setter.Value>
    </Setter>
  </Style>
</ResourceDictionary>
""";
        return (ResourceDictionary)XamlReader.Parse(xaml);
    }

    private void AdaptCompactLayout()
    {
        var compact = ActualHeight < 450;
        if (compact == _compactLayout) return;
        _compactLayout = compact;
        _layout.Margin = compact ? new Thickness(10, 8, 10, 8) : new Thickness(14, 12, 14, 12);
        _input.Height = compact ? 46 : 64;
        _contextNote.Visibility = _inputHint.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        _status.MaxHeight = compact ? 24 : 29;
        RenderAttachments();
    }

    public void Shutdown()
    {
        if (_shutdown) return;
        Cleanup();
        Close();
    }

    private void Cleanup()
    {
        if (_shutdown) return;
        _voiceChatSessionActive?.Invoke(false);
        CancelModelMemoryOperation();
        DisposeVoiceInput();
        _stopSpeech?.Invoke();
        SaveCurrentDraft(); _draftTimer.Stop();
        _shutdown = true;
        InvalidateBehaviorContext(); NotifyBehaviorAvailability();
        CancelConfiguration(); _connectionWindow?.Close(); _libraryWindow?.Close(); _memoryWindow?.Close();
        _lifetime.Cancel(); _generationCancellation?.Cancel();
        _streamTimer.Stop(); _client.Dispose(); _webClient.Dispose(); _setBusy(false);
        _chatStore.Dispose();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_shutdown) return;
        e.Cancel = true;
        CancelVoiceInput();
        _stopSpeech?.Invoke();
        CancelConfiguration(); _connectionWindow?.Close();
        _generationCancellation?.Cancel();
        SaveCurrentDraft();
        Hide();
    }

    private void AddWelcome()
    {
        var welcome = _welcome = Ui.Text(CurrentPersona().Welcome, 13, "#55734F");
        welcome.TextWrapping = TextWrapping.Wrap; welcome.LineHeight = 22; welcome.Margin = new Thickness(2, 4, 2, 12); _messages.Children.Add(welcome);
    }

    private PetPersonaProfile CurrentPersona() => PetPersona.ForSkin(_settings.SkinId, _characterName(), _model, _client.Endpoint.IsLoopback, _capabilities.SupportsImages, _settings.WebSearchEnabled, _settings.LlmBehaviorEnabled && BehaviorServiceReady,
        explicitCommandsEnabled: _settings.LlmBehaviorEnabled && BehaviorCommandsReady);

    private string ConnectionNote() => (_settings.WebSearchEnabled ? "按需联网 · " : "联网关闭 · ") + (_client.Endpoint.IsLoopback
        ? "模型在本机运行"
        : "模型位于 " + _client.Endpoint.GetLeftPart(UriPartial.Authority));

    private void ChangeWebSetting()
    {
        bool enabled = _webEnabled.IsChecked == true;
        if (enabled == _settings.WebSearchEnabled) return;
        if (_generating || _switching || _configuring || _shutdown || ModelMemoryBusy) { _webEnabled.IsChecked = _settings.WebSearchEnabled; return; }
        bool previous = _settings.WebSearchEnabled;
        try { _settings.WebSearchEnabled = enabled; _saveSettings(); }
        catch { _settings.WebSearchEnabled = previous; _webEnabled.IsChecked = previous; SetStatus("联网设置未能保存，请重试。", true); return; }
        _contextNote.Text = ConnectionNote();
        SetStatus(enabled ? "已开启按需联网，搜索词会发往搜索服务；可以直接发送网页链接。" : "已关闭联网，本轮只使用模型知识和已有对话。");
    }

    private string ConnectionTarget() => (_client.Endpoint.IsLoopback ? "本机 Ollama：" : "") + _client.Endpoint.GetLeftPart(UriPartial.Authority);

    private void RefreshWelcome()
    {
        if (_history.Count == 0 && _welcome != null && !_generating) _welcome.Text = CurrentPersona().Welcome;
    }

    private void PersistModelChoice(string selected)
    {
        if (selected.Length == 0 || _settings.OllamaModel == selected) return;
        string previous = _settings.OllamaModel;
        try { _settings.OllamaModel = selected; _saveSettings(); }
        catch { _settings.OllamaModel = previous; throw; }
    }

    internal event Action? BehaviorAvailabilityChanged;
    internal event Action? BehaviorContextInvalidated;

    // Readiness describes the connection and capabilities, independently of a
    // foreground generation. Controllers must not mistake user priority for a disconnect.
    internal bool BehaviorCommandsReady => !_shutdown && _behaviorHost != null && _connected &&
        _installedModels.Contains(_model, StringComparer.Ordinal) && _capabilities.SupportsChat != false;
    // A passive reaction may be plain speech. Tool support is only required
    // for the separate authority to choose autonomous pet actions.
    internal bool BehaviorReactionReady => BehaviorCommandsReady;
    internal bool BehaviorServiceReady => BehaviorCommandsReady && _capabilities.SupportsTools == true;

    internal bool CanReactToInteraction => BehaviorReactionReady && _settings.LlmBehaviorEnabled &&
        !_generating && !_userSendPending && !_switching && !_configuring && !_sessionSwitching && !_preparingImage && !VoiceInputBusy && !ModelMemoryBusy &&
        (_behaviorTask == null || _behaviorTask.IsCompleted) &&
        !(IsVisible && (_input.IsKeyboardFocusWithin || _imeComposing || DateTimeOffset.UtcNow - _lastInputActivity < TimeSpan.FromSeconds(3)));

    internal void RefreshBehaviorConfiguration()
    {
        if (_behaviorSetting != _settings.LlmBehaviorEnabled)
        {
            _behaviorSetting = _settings.LlmBehaviorEnabled;
            InvalidateBehaviorContext();
        }
        NotifyBehaviorAvailability();
    }

    private void NotifyBehaviorAvailability()
    {
        bool ready = BehaviorServiceReady;
        bool commandsReady = BehaviorCommandsReady;
        if (ready == _lastBehaviorAvailability && commandsReady == _lastBehaviorCommandsAvailability) return;
        _lastBehaviorAvailability = ready;
        _lastBehaviorCommandsAvailability = commandsReady;
        BehaviorAvailabilityChanged?.Invoke();
    }

    private void InvalidateBehaviorContext()
    {
        CancelBehaviorReaction();
        BehaviorContextInvalidated?.Invoke();
    }

    internal void CancelBehaviorReaction()
    {
        Interlocked.Increment(ref _behaviorEpoch);
        try { _behaviorCancellation?.Cancel(); } catch (ObjectDisposedException) { }
    }

    internal async Task StopBehaviorReactionAsync()
    {
        CancelBehaviorReaction();
        var pending = _behaviorTask;
        if (pending != null) await pending;
    }

    internal async Task<string?> ReactToInteractionAsync(PetInteractionEvent interaction, CancellationToken cancellationToken)
    {
        if (!Dispatcher.CheckAccess())
            return await Dispatcher.InvokeAsync(() => ReactToInteractionAsync(interaction, cancellationToken)).Task.Unwrap();
        RefreshBehaviorConfiguration();
        if (!CanReactToInteraction || cancellationToken.IsCancellationRequested) return null;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromSeconds(35));
        _behaviorCancellation = cancellation;
        long epoch = Interlocked.Read(ref _behaviorEpoch);
        _behaviorTask = RunBehaviorReactionAsync(interaction, CurrentPersona(), _client, _model, _conversation?.Id, epoch, cancellation);
        return await _behaviorTask;
    }

    private async Task<string?> RunBehaviorReactionAsync(PetInteractionEvent interaction, PetPersonaProfile persona,
        IOllamaClient client, string model, string? conversationId, long epoch, CancellationTokenSource cancellation)
    {
        bool entered = false;
        try
        {
            await _modelCallGate.WaitAsync(cancellation.Token); entered = true;
            cancellation.Token.ThrowIfCancellationRequested();
            if (epoch != Interlocked.Read(ref _behaviorEpoch)) return null;
            _lastUsedModel = model;
            string? reaction = await PetBehaviorPlanner.ReactAsync(client, model, persona, interaction, _behaviorHost!, _capabilities.SupportsTools, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_shutdown || epoch != Interlocked.Read(ref _behaviorEpoch) || !_settings.LlmBehaviorEnabled ||
                !ReferenceEquals(client, _client) || model != _model || conversationId != _conversation?.Id ||
                !string.Equals(persona.Key, _settings.SkinId, StringComparison.OrdinalIgnoreCase)) return null;
            if (reaction == null) { _connected = false; RefreshControls(); return null; }
            if (string.IsNullOrWhiteSpace(reaction)) return null;
            return reaction.Length <= 180 ? reaction : reaction[..180];
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception ex)
        {
            if (!_shutdown && epoch == Interlocked.Read(ref _behaviorEpoch) && ReferenceEquals(client, _client))
            { _connected = false; RefreshControls(); }
            Debug.WriteLine("Pet behavior reaction failed: " + ex.GetType().Name);
            return null;
        }
        finally
        {
            if (entered) _modelCallGate.Release();
            if (ReferenceEquals(_behaviorCancellation, cancellation)) _behaviorCancellation = null;
            cancellation.Dispose();
        }
    }

    internal string? CurrentConversationId => _conversation?.Id;

    private static string EndpointScope(Uri endpoint) => endpoint.GetLeftPart(UriPartial.Authority);
    private string StorageModel => string.IsNullOrEmpty(_model) ? OllamaModels.Normalize(_settings.OllamaModel) : _model;

    private ChatConversation GetScopeConversation(Uri endpoint, string personaKey)
    {
        string scope = EndpointScope(endpoint);
        var active = _chatStore.GetActiveConversationId(scope, personaKey);
        var conversation = active == null ? null : _chatStore.GetConversation(active);
        return conversation ?? _chatStore.CreateConversation(scope, personaKey, StorageModel);
    }

    private void QueueDraftSave()
    {
        if (_shutdown || _restoringConversation || _generating || _sessionSwitching || _conversation == null) return;
        _draftTimer.Stop(); _draftTimer.Start();
    }

    internal bool SaveCurrentDraft()
    {
        _draftTimer.Stop();
        if (_shutdown || _restoringConversation || _conversation == null) return _conversation != null;
        try
        {
            _chatStore.SaveDraft(_conversation.Id, _generating ? _inflightDraft : _input.Text,
                (_generating ? _inflightImages : _pendingImages.ToArray()).Select(image => image.ToStored()).ToArray());
            _storageError = null; return true;
        }
        catch (Exception ex) { ReportStorageFailure(ex); return false; }
    }

    internal bool SaveDraftForTesting() => SaveCurrentDraft();

    private void ReportStorageFailure(Exception ex)
    {
        _storageError = "本地记录未能保存或读取，输入仍保留。请检查磁盘空间和文件权限后重试。";
        SetStatus(_storageError, true);
        Debug.WriteLine("Chat storage operation failed: " + ex.GetType().Name);
    }

    private void RestoreConversation(ChatConversation conversation)
    {
        CancelVoiceInput();
        var turns = _chatStore.ReadTurns(conversation.Id, HistoryPageSize);
        var decoded = turns.Select(turn => (Turn: turn, Images: turn.Images.Select(OllamaChatImage.FromStored).ToArray())).ToArray();
        var draftImages = conversation.DraftImages.Select(OllamaChatImage.FromStored).ToArray();
        _chatStore.SetActiveConversation(conversation.Id);
        if (_conversation?.Id != conversation.Id) InvalidateBehaviorContext();
        _draftTimer.Stop(); _restoringConversation = true;
        try
        {
            _conversation = conversation; _personaKey = conversation.PersonaKey;
            _history.Clear(); _messages.Children.Clear(); _welcome = null;
            _messages.Children.Add(_loadOlder); _loadOlder.Visibility = turns.Count >= HistoryPageSize ? Visibility.Visible : Visibility.Collapsed;
            if (turns.Count == 0) AddWelcome();
            foreach (var item in decoded) RenderStoredTurn(item.Turn, item.Images);
            _input.Text = conversation.Draft; _pendingImages.Clear(); _pendingImages.AddRange(draftImages); RenderAttachments();
            _conversationTitle.Text = conversation.Title; _conversationTitle.ToolTip = conversation.Title;
            _storageError = null; _scroll.ScrollToEnd();
        }
        finally { _restoringConversation = false; }
    }

    private void RenderStoredTurn(ChatStoredTurn turn, IReadOnlyList<OllamaChatImage> images)
    {
        var userView = AddMessage("你", turn.User, true, images, out _);
        var assistantView = AddMessage(_characterName(), turn.Assistant, false, Array.Empty<OllamaChatImage>(), out _);
        AddSources(assistantView, turn.Sources); AddWebWarning(assistantView, turn.WebWarning);
        _history.Add(new ChatTurn(new OllamaMessage("user", turn.User, images.Count == 0 ? null : images.Select(image => image.Base64).ToArray()),
            new OllamaMessage("assistant", PetWebAnswerContext.Remember(turn.Assistant, turn.Sources, turn.WebWarning)), userView, assistantView, _personaKey, turn.Sequence));
    }

    internal void LoadOlderMessages()
    {
        if (_conversation == null || _history.Count == 0 || _generating || _sessionSwitching || _shutdown) return;
        try
        {
            var turns = _chatStore.ReadTurns(_conversation.Id, HistoryPageSize, _history[0].Sequence);
            var decoded = turns.Select(turn => (Turn: turn, Images: turn.Images.Select(OllamaChatImage.FromStored).ToArray())).ToArray();
            double oldHeight = _scroll.ExtentHeight, oldOffset = _scroll.VerticalOffset;
            int insertion = _messages.Children.IndexOf(_loadOlder) + 1;
            var previous = _history.ToArray(); _history.Clear();
            foreach (var item in decoded)
            {
                RenderStoredTurn(item.Turn, item.Images);
                var rendered = _history[^1]; _messages.Children.Remove(rendered.UserView); _messages.Children.Remove(rendered.AssistantView);
                _messages.Children.Insert(insertion++, rendered.UserView); _messages.Children.Insert(insertion++, rendered.AssistantView);
            }
            _history.AddRange(previous); _loadOlder.Visibility = turns.Count >= HistoryPageSize ? Visibility.Visible : Visibility.Collapsed;
            _scroll.UpdateLayout(); _scroll.ScrollToVerticalOffset(oldOffset + _scroll.ExtentHeight - oldHeight);
            SetStatus(turns.Count == 0 ? "已经到最早的记录。" : $"已加载更早的 {turns.Count} 轮；发送时仍只按上下文容量选择最近记录。");
        }
        catch (Exception ex) { ReportStorageFailure(ex); }
    }

    internal void RefreshCharacter()
    {
        RefreshBehaviorConfiguration();
        if (_shutdown || _personaKey == _settings.SkinId.ToLowerInvariant() || _sessionSwitching || _switching || _configuring || _preparingImage) return;
        CancelModelMemoryOperation();
        CancelVoiceInput();
        InvalidateBehaviorContext();
        _sessionTransition = ChangePersonaConversationAsync();
    }

    private async Task ChangePersonaConversationAsync()
    {
        _sessionSwitching = true; RefreshControls();
        try
        {
            await StopGenerationAsync();
            if (_shutdown || !SaveCurrentDraft()) return;
            RestoreConversation(GetScopeConversation(_client.Endpoint, _settings.SkinId.ToLowerInvariant()));
            _contextNote.Text = "已恢复此角色的最近对话 · 本地自动保存";
            SetStatus("已切换角色，可从“历史”继续其他对话。");
        }
        catch (Exception ex) { if (!_shutdown) ReportStorageFailure(ex); }
        finally { _sessionSwitching = false; if (!_shutdown) { RefreshControls(); if (_storageError == null) RefreshCharacter(); } }
    }

    internal async Task SwitchConversationAsync(string conversationId)
    {
        if (_shutdown || _sessionSwitching || _switching || _preparingImage || _configuring || ModelMemoryBusy) return;
        _sessionSwitching = true; RefreshControls();
        try
        {
            await StopGenerationAsync();
            if (_shutdown || !SaveCurrentDraft()) return;
            var conversation = _chatStore.GetConversation(conversationId) ?? throw new InvalidDataException("该对话已被删除。");
            if (conversation.Endpoint != EndpointScope(_client.Endpoint) || conversation.PersonaKey != _settings.SkinId.ToLowerInvariant())
                throw new InvalidDataException("只能打开当前服务器与角色的对话。");
            RestoreConversation(conversation); SetStatus("已恢复对话和草稿，可以继续聊天。");
        }
        catch (Exception ex) { if (!_shutdown) ReportStorageFailure(ex); }
        finally { _sessionSwitching = false; if (!_shutdown) { RefreshControls(); if (_storageError == null) RefreshCharacter(); } }
    }

    private async Task OpenLibraryAsync()
    {
        if (_shutdown || _sessionSwitching || _switching || _preparingImage || _configuring) return;
        _sessionSwitching = true; RefreshControls();
        try
        {
            await StopGenerationAsync();
            if (_shutdown || _conversation != null && !SaveCurrentDraft()) return;
            var window = new ChatLibraryWindow(_chatStore, EndpointScope(_client.Endpoint), _personaKey, StorageModel, _conversation?.Id) { Owner = this, Topmost = Topmost };
            _libraryWindow = window; window.ShowDialog(); _libraryWindow = null;
            if (_shutdown) return;
            if (window.SelectedConversationId is string selected)
            {
                var conversation = _chatStore.GetConversation(selected);
                if (conversation != null) RestoreConversation(conversation);
            }
            else if (_conversation != null)
            {
                var current = _chatStore.GetConversation(_conversation.Id);
                if (current != null) { _conversation = current; _conversationTitle.Text = current.Title; _conversationTitle.ToolTip = current.Title; }
                else RestoreConversation(GetScopeConversation(_client.Endpoint, _personaKey));
            }
            SetStatus("对话记录保存在本机；不同角色和服务器的记录分开。");
        }
        catch (Exception ex) { if (!_shutdown) ReportStorageFailure(ex); }
        finally { _libraryWindow = null; _sessionSwitching = false; if (!_shutdown) { RefreshControls(); if (_storageError == null) RefreshCharacter(); } }
    }

    private void OpenMemory(string? initialContent = null)
    {
        if (_shutdown || _generating || _sessionSwitching || _switching || _configuring) return;
        try
        {
            var window = new SharedMemoryWindow(_chatStore, EndpointScope(_client.Endpoint), _settings.SharedMemoryEnabled, enabled =>
            {
                bool previous = _settings.SharedMemoryEnabled;
                try { _settings.SharedMemoryEnabled = enabled; _saveSettings(); }
                catch { _settings.SharedMemoryEnabled = previous; throw; }
            }, initialContent) { Owner = this, Topmost = Topmost };
            _memoryWindow = window; window.ShowDialog(); _memoryWindow = null;
            if (!_shutdown) SetStatus(_settings.SharedMemoryEnabled ? "共享记忆已开启：只取相关记忆用于回复，可随时编辑或删除。" : "共享记忆已关闭，已保存内容仍可在记忆库管理。");
        }
        catch (Exception ex) { if (!_shutdown) ReportStorageFailure(ex); }
        finally { _memoryWindow = null; }
    }

    private void SaveExplicitMemory(string content)
    {
        if (_conversation == null || !SaveCurrentDraft()) return;
        bool memorySaved = false;
        try
        {
            string endpoint = EndpointScope(_client.Endpoint);
            var existing = _chatStore.ListMemories(endpoint).FirstOrDefault(memory => memory.Content.Equals(content, StringComparison.OrdinalIgnoreCase));
            var memory = new ChatSharedMemory(existing?.Id ?? Guid.NewGuid().ToString("N"), endpoint,
                content.Length > 40 ? content[..40] + "…" : content, content, existing?.Pinned ?? false, DateTimeOffset.UtcNow);
            _chatStore.UpsertMemory(memory); memorySaved = true;
            string prompt = _input.Text.Trim();
            string answer = "已保存到共享记忆：" + content + (_settings.SharedMemoryEnabled ? "\n此服务器的其他对话也可使用；在“记忆”中可以修改或删除。" : "\n当前未启用共享记忆；可在“记忆”中开启、修改或删除。");
            var stored = new ChatStoredTurn(0, prompt, answer, StorageModel, Array.Empty<ChatStoredImage>(), Array.Empty<PetWebSource>(), null, DateTimeOffset.UtcNow);
            long sequence = _chatStore.AppendTurn(_conversation.Id, stored);
            RenderStoredTurn(stored with { Sequence = sequence }, Array.Empty<OllamaChatImage>());
            LimitVisibleHistory();
            _restoringConversation = true;
            try { _input.Clear(); } finally { _restoringConversation = false; }
            UpdateConversationTitle(); _scroll.ScrollToEnd(); _storageError = null;
            SetStatus("记忆已保存在本机，不需要调用模型。可从其他对话使用。"); RefreshControls();
        }
        catch (Exception ex)
        {
            ReportStorageFailure(ex);
            if (memorySaved) SetStatus("共享记忆已保存，但本条对话记录未能保存；输入仍保留。", true);
        }
    }

    private void UpdateConversationTitle()
    {
        if (_conversation == null) return;
        var updated = _chatStore.GetConversation(_conversation.Id);
        if (updated != null) { _conversation = updated; _conversationTitle.Text = updated.Title; _conversationTitle.ToolTip = updated.Title; }
    }

    private void LimitVisibleHistory()
    {
        // This only releases old rendered views; SQLite keeps every completed turn.
        // The archive button can page them back into view at any time.
        bool removed = false;
        while (_history.Count > HistoryPageSize)
        {
            var oldest = _history[0]; _history.RemoveAt(0);
            _messages.Children.Remove(oldest.UserView); _messages.Children.Remove(oldest.AssistantView); removed = true;
        }
        if (removed) _loadOlder.Visibility = Visibility.Visible;
    }

    private static void AddWebWarning(FrameworkElement view, string? warningText)
    {
        if (warningText == null || view is not Border { Child: StackPanel contents }) return;
        var warning = Ui.Text("联网提示：" + warningText, 11, "#A44F39"); warning.Name = "OllamaWebWarning";
        warning.TextWrapping = TextWrapping.Wrap; warning.Margin = new Thickness(0, 9, 0, 0); contents.Children.Add(warning);
    }

    private void RefreshControls()
    {
        if (_send == null) return;
        RefreshBehaviorConfiguration();
        bool sendStarting = _userSendPending && !_generating;
        bool composing = !_generating && !_userSendPending && !_switching && !_preparingImage && !_shutdown && !_configuring && !_sessionSwitching && !VoiceInputBusy && !ModelMemoryBusy;
        bool correctConversation = _conversation != null && _conversation.Endpoint == EndpointScope(_client.Endpoint) && _conversation.PersonaKey == _settings.SkinId.ToLowerInvariant();
        bool chatAvailable = _connected && _installedModels.Contains(_model, StringComparer.Ordinal) && _capabilities.SupportsChat != false;
        bool imagesAvailable = chatAvailable && _capabilities.SupportsImages != false;
        _send.IsEnabled = composing && correctConversation && (chatAvailable || SharedMemoryContext.TryExtractExplicitMemory(_input.Text, out _)) && (_pendingImages.Count == 0 || imagesAvailable) && (!string.IsNullOrWhiteSpace(_input.Text) || _pendingImages.Count > 0);
        _input.IsReadOnly = !composing;
        _attach.IsEnabled = _paste.IsEnabled = composing && imagesAvailable && _pendingImages.Count < OllamaChatImage.MaxFiles;
        _stop.IsEnabled = (_generating && !_switching) || ModelMemoryBusy;
        _newChat.IsEnabled = _library.IsEnabled = !sendStarting && !_switching && !_preparingImage && !_shutdown && !_configuring && !_sessionSwitching && !ModelMemoryBusy;
        _memory.IsEnabled = composing;
        _loadOlder.IsEnabled = composing;
        _refresh.IsEnabled = composing;
        _models.IsEnabled = _connected && !sendStarting && !_switching && !_preparingImage && !_shutdown && !_configuring && !_sessionSwitching && _installedModels.Count > 0 && !ModelMemoryBusy;
        _connection.IsEnabled = !sendStarting && !_switching && !_preparingImage && !_shutdown && !_configuring && !_sessionSwitching && !ModelMemoryBusy;
        _connection.ToolTip = "Ollama 地址与端口设置\n当前：" + _client.Endpoint.GetLeftPart(UriPartial.Authority);
        _attachments.IsEnabled = composing;
        _webEnabled.IsEnabled = composing;
        bool textOnly = _capabilities.SupportsImages == false;
        _placeholder.Text = _capabilities.SupportsChat == false ? "请切换到支持聊天的模型…" : textOnly ? "说点什么，和我聊聊…" : "说点什么，或拖入图片…";
        _attachmentHint.Text = _capabilities.SupportsChat == false ? "此模型不支持聊天" : textOnly ? "当前模型仅支持文字" : _capabilities.SupportsImages == null ? "可尝试发送图片" : "拖入图片也可以";
        _input.ToolTip = (textOnly ? "当前模型仅支持文字。" : "支持拖入或 Ctrl+V 粘贴图片。") + "Enter 发送，Shift+Enter 换行。";
        RefreshVoiceInputControls();
        RefreshModelMemoryControls();
    }

    private (CancellationTokenSource Cancellation, int Revision) BeginConfiguration(CancellationToken additional = default)
    {
        CancelModelMemoryOperation();
        CancelVoiceInput();
        CancelBehaviorReaction();
        CancelConfiguration();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, additional);
        cancellation.CancelAfter(TimeSpan.FromSeconds(45));
        _configurationCancellation = cancellation; _switching = true; RefreshControls();
        return (cancellation, _configurationRevision);
    }

    private void CancelConfiguration()
    {
        ++_configurationRevision;
        _configurationCancellation?.Cancel();
        if (_configurationCancellation != null) _connected = false;
        _configurationCancellation = null; _switching = false;
        NotifyBehaviorAvailability();
    }

    private void CheckConfiguration(CancellationTokenSource cancellation, int revision)
    {
        cancellation.Token.ThrowIfCancellationRequested();
        if (_shutdown || revision != _configurationRevision) throw new OperationCanceledException(cancellation.Token);
    }

    private void FinishConfiguration(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_configurationCancellation, cancellation))
        { _configurationCancellation = null; _switching = false; if (!_shutdown) { RefreshControls(); RefreshCharacter(); } }
        cancellation.Dispose();
    }

    private static string ChooseModel(IReadOnlyList<string> installed, string preferred)
    {
        if (installed.Contains(preferred, StringComparer.Ordinal)) return preferred;
        if (installed.Contains(OllamaModels.FastModel, StringComparer.Ordinal)) return OllamaModels.FastModel;
        if (installed.Contains(OllamaModels.QualityModel, StringComparer.Ordinal)) return OllamaModels.QualityModel;
        return installed.FirstOrDefault() ?? "";
    }

    private void SetModelList(IReadOnlyList<string> installed, string selected)
    {
        _installedModels = installed.ToArray();
        _selectionSync = true;
        _models.ItemsSource = installed.Select(id => new OllamaModelChoice(id, OllamaModels.Supported.FirstOrDefault(choice => choice.Id == id)?.DisplayName ?? id)).ToArray();
        _models.SelectedValue = string.IsNullOrEmpty(selected) ? null : selected;
        _selectionSync = false;
    }

    private string ModelReadyMessage(string prefix)
    {
        if (_installedModels.Count == 0) return prefix + " · 还没有已安装模型，请先在 Ollama 下载模型，再刷新。";
        if (_capabilities.SupportsChat == false) return prefix + " · 当前模型不支持聊天，请选择其他模型。";
        if (_capabilities.SupportsImages == false)
            return prefix + (_pendingImages.Count > 0 ? " · 当前模型仅支持文字，请先移除待发送图片。" : " · 当前模型仅支持文字；历史图片轮次不会发送给它。");
        return prefix + (_capabilities.SupportsImages == true ? " · 支持图片，首次回复需加载模型。" : " · 模型能力未完整公布，可尝试聊天或图片。 ");
    }

    internal async Task CheckConnectionAsync()
    {
        if (_generating || _userSendPending || _switching || _preparingImage || _shutdown || _configuring || _sessionSwitching || VoiceInputBusy || ModelMemoryBusy) return;
        var (operation, revision) = BeginConfiguration();
        var client = _client;
        SetStatus("正在刷新 " + ConnectionTarget() + " 的模型列表…");
        try
        {
            await StopBehaviorReactionAsync(); CheckConfiguration(operation, revision);
            var installed = await client.ListModelsAsync(operation.Token);
            CheckConfiguration(operation, revision);
            var selected = ChooseModel(installed, _model.Length == 0 ? _settings.OllamaModel : _model);
            var capabilities = selected.Length == 0 ? new OllamaModelCapabilities(null, null, null) : await client.GetModelInfoAsync(selected, operation.Token);
            CheckConfiguration(operation, revision);
            if (_lastUsedModel != null && _lastUsedModel != selected)
            {
                await client.UnloadAsync(_lastUsedModel, operation.Token);
                CheckConfiguration(operation, revision); _lastUsedModel = null;
            }
            PersistModelChoice(selected);
            if (_model != selected) InvalidateBehaviorContext();
            SetModelList(installed, selected); _model = selected; _capabilities = capabilities; _connected = true; RefreshWelcome();
            _contextNote.Text = ConnectionNote();
            SetStatus(ModelReadyMessage("Ollama 已连接 · " + installed.Count + " 个已安装模型 · " + client.Endpoint.GetLeftPart(UriPartial.Authority)), capabilities.SupportsChat == false || installed.Count == 0);
        }
        catch (OperationCanceledException) { if (!_shutdown && revision == _configurationRevision) { _connected = false; SetStatus("连接检查已取消或超时，请确认服务器地址后刷新。", true); } }
        catch (Exception ex) { if (!_shutdown && revision == _configurationRevision) { _connected = false; SetModelList(Array.Empty<string>(), ""); SetStatus(ex is OllamaException ? ex.Message : "无法连接 " + ConnectionTarget() + "，请检查地址和端口后刷新。", true); } }
        finally { FinishConfiguration(operation); }
    }

    private async Task ChangeModelAsync()
    {
        if (_selectionSync || _shutdown || _models.SelectedValue is not string selected || selected == _model) return;
        if (ModelMemoryBusy) { _selectionSync = true; _models.SelectedValue = _model; _selectionSync = false; return; }
        if (_switching || _configuring || !_connected || !_installedModels.Contains(selected, StringComparer.Ordinal)) return;
        InvalidateBehaviorContext();
        var (operation, revision) = BeginConfiguration();
        var previous = _model;
        var client = _client;
        SetStatus("正在停止回复并释放旧模型显存…");
        try
        {
            await StopGenerationAsync();
            CheckConfiguration(operation, revision);
            if (_lastUsedModel == previous)
            {
                await client.UnloadAsync(previous, operation.Token);
                CheckConfiguration(operation, revision);
                _lastUsedModel = null;
            }
            var capabilities = await client.GetModelInfoAsync(selected, operation.Token);
            CheckConfiguration(operation, revision);
            PersistModelChoice(selected);
            _model = selected; _capabilities = capabilities; RefreshWelcome();
            SetStatus(ModelReadyMessage("模型已切换，保留当前对话"), capabilities.SupportsChat == false || capabilities.SupportsImages == false && _pendingImages.Count > 0);
        }
        catch (OperationCanceledException) { if (!_shutdown && revision == _configurationRevision) SetStatus("切换已取消或超时，已保留原模型。请刷新后重试。", true); }
        catch (Exception ex) { if (!_shutdown && revision == _configurationRevision) SetStatus(ex is OllamaException ? ex.Message + " 已保留原模型。" : "切换失败，已保留原模型。请检查连接后重试。", true); }
        finally
        {
            if (revision == _configurationRevision) { _selectionSync = true; _models.SelectedValue = _model; _selectionSync = false; }
            FinishConfiguration(operation);
        }
    }

    private void OpenConnectionSettings()
    {
        if (_shutdown || _switching || _preparingImage || ModelMemoryBusy) return;
        CancelVoiceInput();
        if (_connectionWindow != null) { _connectionWindow.Activate(); return; }
        CancelBehaviorReaction();
        _configuring = true; RefreshControls();
        var window = new OllamaConnectionWindow(_client.Endpoint, _clientFactory, ApplyConnectionAsync) { Owner = this, Topmost = Topmost };
        _connectionWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_connectionWindow, window)) _connectionWindow = null; _configuring = false; if (!_shutdown) { RefreshControls(); RefreshCharacter(); FocusComposer(); } };
        window.Show();
    }

    private async Task<bool> ApplyConnectionAsync(IOllamaClient candidate, IReadOnlyList<string> installed, CancellationToken cancellationToken)
    {
        if (_shutdown || _connectionWindow == null || !_connectionWindow.IsVisible) return false;
        var (operation, revision) = BeginConfiguration(cancellationToken);
        var previousClient = _client;
        bool serverChanged = previousClient.Endpoint != candidate.Endpoint;
        bool previousConnected = _connected;
        bool adopted = false;
        string unloadWarning = "";
        ChatConversation? destinationConversation = null;
        try
        {
            await StopGenerationAsync(); CheckConfiguration(operation, revision);
            if (!SaveCurrentDraft()) throw new InvalidDataException("无法保存当前草稿，尚未更换服务器。");
            var selected = ChooseModel(installed, _model);
            var capabilities = selected.Length == 0 ? new OllamaModelCapabilities(null, null, null) : await candidate.GetModelInfoAsync(selected, operation.Token);
            CheckConfiguration(operation, revision);
            if (_lastUsedModel != null && (serverChanged || selected != _model))
            {
                using var unloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(operation.Token);
                unloadTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { await previousClient.UnloadAsync(_lastUsedModel, unloadTimeout.Token); }
                catch (OperationCanceledException) when (operation.IsCancellationRequested) { throw; }
                catch (Exception) { unloadWarning = "旧服务器模型未能主动释放，将按其保留时间自动卸载。"; }
                CheckConfiguration(operation, revision);
            }
            CheckConfiguration(operation, revision);
            if (serverChanged)
            {
                destinationConversation = GetScopeConversation(candidate.Endpoint, _settings.SkinId.ToLowerInvariant());
                // Validate the destination before committing a server change. Existing messages are never sent across endpoints.
                foreach (var turn in _chatStore.ReadTurns(destinationConversation.Id, HistoryPageSize))
                    foreach (var image in turn.Images) _ = OllamaChatImage.FromStored(image);
                foreach (var image in destinationConversation.DraftImages) _ = OllamaChatImage.FromStored(image);
            }
            string previousAddress = _settings.OllamaAddress, previousModel = _settings.OllamaModel;
            try
            {
                _settings.OllamaAddress = candidate.Endpoint.GetLeftPart(UriPartial.Authority);
                if (selected.Length > 0) _settings.OllamaModel = selected;
                _saveSettings();
            }
            catch { _settings.OllamaAddress = previousAddress; _settings.OllamaModel = previousModel; throw; }
            if (serverChanged || selected != _model) InvalidateBehaviorContext();
            _client = candidate; adopted = true;
            if (serverChanged || selected != _model) _lastUsedModel = null;
            _model = selected; _capabilities = capabilities; _connected = true; SetModelList(installed, selected);
            if (serverChanged)
            {
                try { RestoreConversation(destinationConversation!); }
                catch (Exception ex) { ReportStorageFailure(ex); }
            }
            else RefreshWelcome();
            _contextNote.Text = ConnectionNote();
            try { previousClient.Dispose(); } catch { /* The new connection is already committed. */ }
            SetStatus(unloadWarning.Length > 0 ? "连接已更换。" + unloadWarning : ModelReadyMessage(serverChanged ? "连接已更换，已恢复此服务器的对话；原记录仍在本机" : "连接设置已应用"), unloadWarning.Length > 0 || capabilities.SupportsChat == false);
            return true;
        }
        catch
        {
            if (!adopted && revision == _configurationRevision) _connected = previousConnected;
            throw;
        }
        finally { FinishConfiguration(operation); }
    }

    private async Task StopGenerationAsync()
    {
        CancelVoiceInput();
        _stopSpeech?.Invoke();
        _generationCancellation?.Cancel();
        CancelBehaviorReaction();
        if (_generationTask != null) await _generationTask;
        await StopBehaviorReactionAsync();
    }

    internal async Task NewConversationAsync()
    {
        if (_switching || _preparingImage || _shutdown || _configuring || _sessionSwitching || ModelMemoryBusy) return;
        _sessionSwitching = true; RefreshControls();
        try
        {
            await StopGenerationAsync();
            if (_shutdown || _conversation != null && !SaveCurrentDraft()) return;
            RestoreConversation(_chatStore.CreateConversation(EndpointScope(_client.Endpoint), _settings.SkinId.ToLowerInvariant(), StorageModel));
            _contextNote.Text = "已开始新对话 · " + ConnectionNote();
            SetStatus("新对话已就绪；之前的记录可以从“历史”继续。"); _input.Focus();
        }
        catch (Exception ex) { if (!_shutdown) ReportStorageFailure(ex); }
        finally { _sessionSwitching = false; if (!_shutdown) { RefreshControls(); if (_storageError == null) RefreshCharacter(); } }
    }

    internal async Task SendAsync()
    {
        if (_userSendPending || _generating || _shutdown || VoiceInputBusy || ModelMemoryBusy) return;
        _stopSpeech?.Invoke();
        _userSendPending = true; RefreshControls();
        try
        {
            await StopBehaviorReactionAsync();
            await SendCoreAsync();
        }
        finally { _userSendPending = false; if (!_shutdown) RefreshControls(); }
    }

    private async Task SendCoreAsync()
    {
        RefreshCharacter();
        if (_sessionTransition != null) await _sessionTransition;
        if (_generating || _switching || _preparingImage || _shutdown || _configuring || _sessionSwitching || ModelMemoryBusy) return;
        if (_conversation == null || _conversation.Endpoint != EndpointScope(_client.Endpoint) || _conversation.PersonaKey != _settings.SkinId.ToLowerInvariant())
        { SetStatus("请先恢复当前角色与服务器的对话。", true); return; }
        if (SharedMemoryContext.TryExtractExplicitMemory(_input.Text, out var memoryContent))
        {
            if (_pendingImages.Count > 0) { SetStatus("保存记忆时请先移除图片；记忆库只保存你明确写下的文字。", true); return; }
            SaveExplicitMemory(memoryContent); return;
        }
        string memoryRequest = _input.Text.Trim();
        if (memoryRequest.StartsWith("记住：", StringComparison.Ordinal) || memoryRequest.StartsWith("记住:", StringComparison.Ordinal) || memoryRequest.StartsWith("请记住", StringComparison.Ordinal))
        {
            SetStatus(SharedMemoryContext.ContainsLikelySecret(memoryRequest) ? "这段内容疑似含密码或密钥，未写入共享记忆；输入仍保留。" : "请在“记住：”后填写 1–1000 字的记忆内容。输入仍保留。", true);
            return;
        }
        if (!_connected || !_installedModels.Contains(_model, StringComparer.Ordinal)) { SetStatus("请先连接 Ollama 并选择已安装模型。", true); return; }
        if (_capabilities.SupportsChat == false) { SetStatus("当前模型不支持聊天，请选择其他模型。", true); return; }
        if (_capabilities.SupportsImages == false && _pendingImages.Count > 0) { SetStatus("当前模型仅支持文字，请先移除待发送图片，或切换支持图片的模型。", true); return; }
        var draft = _input.Text;
        if (string.IsNullOrWhiteSpace(draft) && _pendingImages.Count == 0) return;
        var prompt = string.IsNullOrWhiteSpace(draft) ? "请描述这张图片。" : draft.Trim();
        if (prompt.Length > MaxInputCharacters) { SetStatus("每条消息最多 3000 字符。", true); return; }
        var images = _pendingImages.ToArray();
        var persona = CurrentPersona();
        try
        {
            if (_settings.SharedMemoryEnabled)
                persona = SharedMemoryContext.Build(persona, prompt, _chatStore.ListMemories(EndpointScope(_client.Endpoint))).Profile;
        }
        catch (Exception ex) { ReportStorageFailure(ex); return; }
        int budget = PetPersona.ConversationBudget(persona) - (_settings.LlmBehaviorEnabled && BehaviorCommandsReady ? MaxBehaviorReceiptCharacters * 2 + 32 : 0);
        if (EstimateTokens(prompt, images.Length) > budget)
        {
            int maximum = Math.Max(0, (budget - images.Length * 1800 - 16) / 2);
            SetStatus($"为角色设定和回复预留空间，本条消息请缩短到 {maximum} 字符以内。输入与图片已保留。", true);
            return;
        }
        if (!SaveCurrentDraft()) return;
        _generationTask = RunTurnAsync(prompt, draft, images, persona, _model, _characterName(), _conversation.Id);
        await _generationTask;
    }

    private async Task RunTurnAsync(string prompt, string draft, OllamaChatImage[] images, PetPersonaProfile persona, string model, string characterName, string conversationId)
    {
        _inflightDraft = draft; _inflightImages = images;
        _generating = true; _setBusy(true);
        _generationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _generationCancellation;
        RefreshControls();
        var user = new OllamaMessage("user", prompt, images.Length == 0 ? null : images.Select(x => x.Base64).ToArray());
        var userView = AddMessage("你", prompt, true, images, out _);
        var assistantView = AddMessage(characterName, persona.Thinking, false, Array.Empty<OllamaChatImage>(), out var answerView);
        _input.Clear(); _pendingImages.Clear(); RenderAttachments();
        _streamView = answerView;
        lock (_streamLock) _streamText.Clear();
        _streamTimer.Start(); SetStatus("正在生成… 首次加载稍慢，可随时停止。");
        _scroll.ScrollToEnd();
        bool completedButUnsaved = false;
        bool modelGateEntered = false;
        try
        {
            await _modelCallGate.WaitAsync(cancellation.Token); modelGateEntered = true;
            var context = BuildContext(user, persona, out var dropped);
            _lastUsedModel = model;
            var client = _client;
            if (_settings.LlmBehaviorEnabled && BehaviorCommandsReady && _behaviorHost != null)
            {
                SetStatus("正在理解你的意思并检查可执行动作…");
                // This phase runs before any new web lookup. The planner itself
                // excludes memory/system text and external evidence as instructions.
                var plan = await PetBehaviorPlanner.PlanConversationAsync(client, model, context, _behaviorHost, _capabilities.SupportsTools, cancellation.Token);
                cancellation.Token.ThrowIfCancellationRequested();
                string receipt = plan.Instruction.Length <= MaxBehaviorReceiptCharacters ? plan.Instruction : plan.Instruction[..MaxBehaviorReceiptCharacters];
                persona = persona with { SystemPrompt = persona.SystemPrompt + "\n" + receipt };
                context = BuildContext(user, persona, out dropped);
            }
            if (dropped > 0) _contextNote.Text = $"本次上下文省略了最早 {dropped} 轮，保留最近完整对话。" + (_capabilities.SupportsImages == false ? "图片历史轮次不发送。" : "");
            else _contextNote.Text = (_capabilities.SupportsImages == false ? "图片历史轮次不发送 · " : "") + ConnectionNote();
            PetWebChatUpdate? completed = null;
            IReadOnlyList<PetWebSource> sources = Array.Empty<PetWebSource>();
            string? webWarning = null;
            bool webEnabled = _settings.WebSearchEnabled;
            bool? supportsTools = _capabilities.SupportsTools;
            BeginStreamingSpeech();
            await Task.Run(async () =>
            {
                await foreach (var delta in PetWebChat.RunAsync(client, _webClient, model, context, webEnabled, supportsTools, cancellation.Token).ConfigureAwait(false))
                {
                    cancellation.Token.ThrowIfCancellationRequested();
                    if (delta.Status != null) await Dispatcher.InvokeAsync(() => { if (!_shutdown && !cancellation.IsCancellationRequested) SetStatus(delta.Status); });
                    PetWebAnswerContext.ApplyMetadata(delta, ref sources, ref webWarning);
                    lock (_streamLock)
                    {
                        if (_streamText.Length + (delta.Content?.Length ?? 0) > 16000) throw new InvalidDataException("回复超过安全长度，请缩短问题后重试。");
                        _streamText.Append(delta.Content);
                    }
                    if (!string.IsNullOrEmpty(delta.Content))
                        await Dispatcher.InvokeAsync(() => { if (!_shutdown && !cancellation.IsCancellationRequested) AppendStreamingSpeech(delta.Content); });
                    if (delta.Done) completed = delta;
                }
            }, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_shutdown || _conversation?.Id != conversationId || !string.Equals(_settings.SkinId, persona.Key, StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException();
            if (completed == null) throw new InvalidDataException("模型回复中断，请重试。");
            string answer; lock (_streamLock) answer = _streamText.ToString();
            if (string.IsNullOrWhiteSpace(answer)) throw new InvalidDataException("模型返回了空回复，请重试。");
            long sequence;
            try
            {
                sequence = _chatStore.AppendTurn(conversationId, new ChatStoredTurn(0, prompt, answer, model,
                    images.Select(image => image.ToStored()).ToArray(), sources, webWarning, DateTimeOffset.UtcNow));
                _storageError = null;
            }
            catch (Exception ex)
            {
                completedButUnsaved = true; FlushStream(); AddSources(assistantView, sources); AddWebWarning(assistantView, webWarning);
                if (assistantView is Border { Child: StackPanel unsavedContents })
                    unsavedContents.Children.Add(Ui.Text("此回复尚未保存到本地，请先复制需要保留的内容。", 11, "#A44F39"));
                throw new InvalidDataException("回复已生成，但本地记录未能保存；输入已保留，请检查磁盘后重试。", ex);
            }
            try { UpdateConversationTitle(); } catch (Exception ex) { ReportStorageFailure(ex); }
            FlushStream();
            AddSources(assistantView, sources);
            AddWebWarning(assistantView, webWarning);
            _history.Add(new ChatTurn(user, new OllamaMessage("assistant", PetWebAnswerContext.Remember(answer, sources, webWarning)), userView, assistantView, persona.Key, sequence));
            LimitVisibleHistory();
            var rate = completed.TokensPerSecond is > 0 ? $" · {completed.TokensPerSecond:0.0} tokens/秒" : "";
            SetStatus("回复完成" + rate + (webWarning != null ? " · 联网受限，详情见回复下方提示。" : sources.Count > 0 ? $" · 已附 {sources.Count} 个网页来源。" : _capabilities.SupportsImages == false ? " · 可继续文字聊天。" : " · 可继续聊天或发送图片。"), webWarning != null);
            string bubbleText = answer.Length <= 100 ? answer : answer[..100] + "…";
            // Streamed speech owns this turn when enabled. Keep the legacy final
            // callback for callers that have not supplied streaming playback.
            if (!FinishStreamingSpeech(bubbleText))
            {
                if (_answerCompleted != null) _answerCompleted(answer, bubbleText);
                else _say(bubbleText);
            }
        }
        catch (Exception ex)
        {
            CancelStreamingSpeech();
            if (!_shutdown && _conversation?.Id == conversationId)
            {
                if (!completedButUnsaved) { _messages.Children.Remove(userView); _messages.Children.Remove(assistantView); }
                _input.Text = draft; _pendingImages.AddRange(images); RenderAttachments();
                SetStatus(ex is OperationCanceledException ? "已停止。本条消息和图片已放回输入区，可修改后重新发送。" : FriendlyFailure(ex), ex is not OperationCanceledException);
            }
        }
        finally
        {
            if (modelGateEntered) _modelCallGate.Release();
            _streamTimer.Stop(); _streamView = null; lock (_streamLock) _streamText.Clear();
            cancellation.Dispose(); if (ReferenceEquals(_generationCancellation, cancellation)) _generationCancellation = null;
            _generating = false; _setBusy(false);
            _inflightDraft = ""; _inflightImages = Array.Empty<OllamaChatImage>();
            if (!_shutdown) { SaveCurrentDraft(); RefreshControls(); _input.Focus(); }
        }
    }

    private IReadOnlyList<OllamaMessage> BuildContext(OllamaMessage user, PetPersonaProfile persona, out int dropped)
    {
        var retained = _history.Where(turn => turn.PersonaKey == persona.Key && (_capabilities.SupportsImages != false || (turn.User.Images?.Count ?? 0) == 0)).ToList(); dropped = 0;
        int budget = PetPersona.ConversationBudget(persona);
        while (retained.Count > 0 && (retained.Count >= MaxHistoryTurns || retained.Sum(x => EstimateTokens(x.User.Content, x.User.Images?.Count ?? 0) + EstimateTokens(x.Assistant.Content, 0)) + EstimateTokens(user.Content, user.Images?.Count ?? 0) > budget || retained.Sum(x => x.User.Images?.Count ?? 0) + (user.Images?.Count ?? 0) > MaxHistoryImages))
        { retained.RemoveAt(0); dropped++; }
        var messages = new List<OllamaMessage> { new("system", persona.SystemPrompt) };
        foreach (var turn in retained) { messages.Add(turn.User); messages.Add(turn.Assistant); }
        messages.Add(user); return messages;
    }

    // A conservative bound for Chinese text and 1280-pixel images; reserve the remainder of 8K for system text and output.
    private static int EstimateTokens(string content, int imageCount) => PetPersona.EstimateTokens(content, imageCount);

    private static string FriendlyFailure(Exception ex)
    {
        if (ex is InvalidDataException || ex is OllamaException) return ex.Message + " 本条消息已放回输入区。";
        return "回复失败：请确认 Ollama 正在运行且已安装所选模型；也可能显存不足。消息与图片已保留，可重试。";
    }

    private FrameworkElement AddMessage(string who, string text, bool user, IReadOnlyList<OllamaChatImage> images, out TextBox body)
    {
        var contents = new StackPanel();
        var label = Ui.Text(who, 11, user ? "#53705A" : "#477247", true); label.Margin = new Thickness(0, 0, 0, 6); contents.Children.Add(label);
        if (images.Count > 0)
        {
            var thumbnails = new WrapPanel();
            foreach (var image in images) thumbnails.Children.Add(new Image { Source = image.Preview, MaxWidth = 115, Height = 86, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 5, 6), ToolTip = image.Name });
            contents.Children.Add(thumbnails);
        }
        body = new TextBox { Text = text, IsReadOnly = true, IsReadOnlyCaretVisible = false, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = Ui.Brush("#294635"), FontSize = 13, AcceptsReturn = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        contents.Children.Add(body);
        var bubble = new Border { Background = Ui.Brush(user ? "#EAF2E5" : "#F4F6F0"), CornerRadius = new CornerRadius(11), Padding = new Thickness(10, 8, 10, 10), Margin = user ? new Thickness(22, 0, 0, 9) : new Thickness(0, 0, 18, 9), Child = contents };
        _messages.Children.Add(bubble); return bubble;
    }

    private void AddSources(FrameworkElement view, IReadOnlyList<PetWebSource> sources)
    {
        if (sources.Count == 0 || view is not Border { Child: StackPanel contents }) return;
        var panel = new StackPanel { Name = "OllamaSources", Margin = new Thickness(0, 9, 0, 0) };
        panel.Children.Add(Ui.Text("网页来源", 10, "#718267", true));
        for (int index = 0; index < Math.Min(6, sources.Count); index++)
        {
            var source = sources[index];
            if (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) continue;
            string title = source.Title.Replace('\r', ' ').Replace('\n', ' ');
            if (title.Length > 100) title = title[..100] + "…";
            var link = new Hyperlink(new Run($"[{index + 1}] {title}")) { NavigateUri = uri, ToolTip = source.Url, Foreground = Ui.Brush("#356446") };
            link.RequestNavigate += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { SetStatus("无法打开浏览器，可以复制回答中的来源。", true); }
            };
            var text = Ui.Text("", 11, "#356446"); text.TextWrapping = TextWrapping.Wrap; text.Margin = new Thickness(0, 4, 0, 0); text.Inlines.Add(link);
            panel.Children.Add(text);
        }
        contents.Children.Add(panel);
    }

    private void FlushStream()
    {
        if (_streamView == null || _shutdown) return;
        string text; lock (_streamLock) text = _streamText.ToString();
        if (text.Length == 0 || text == _streamView.Text) return;
        bool follow = _scroll.ScrollableHeight - _scroll.VerticalOffset < 70;
        _streamView.Text = text;
        if (follow) _scroll.ScrollToEnd();
    }

    private void SetStatus(string text, bool error = false)
    {
        if (_storageError != null && !error) { text = _storageError; error = true; }
        _status.Text = text; _status.Foreground = Ui.Brush(error ? "#A44F39" : "#52734E");
    }

    private async Task PickImagesAsync()
    {
        if (VoiceInputBusy || ModelMemoryBusy) return;
        var picker = new OpenFileDialog { Title = "发送图片给桌宠", Filter = "图片（PNG、JPEG、BMP、GIF）|*.png;*.jpg;*.jpeg;*.bmp;*.gif", Multiselect = true, CheckFileExists = true };
        if (picker.ShowDialog(this) == true) await AddImageFilesAsync(picker.FileNames);
    }

    internal async Task AddImageFilesAsync(IEnumerable<string> paths)
    {
        if (_generating || _switching || _preparingImage || _shutdown || _configuring || _sessionSwitching || VoiceInputBusy || ModelMemoryBusy) return;
        if (!CanAttachImages()) return;
        var files = paths.ToArray();
        if (files.Length + _pendingImages.Count > OllamaChatImage.MaxFiles) { SetStatus("每次最多发送 2 张图片；请先移除一些图片。", true); return; }
        _preparingImage = true; RefreshControls(); SetStatus("正在本机处理图片…");
        int added = 0; bool failed = false;
        try
        {
            foreach (var path in files)
            {
                try { _pendingImages.Add(await OllamaChatImage.FromFileAsync(path, _lifetime.Token)); added++; }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { failed = true; SetStatus(ex is InvalidDataException ? ex.Message : "图片无法读取或文件损坏，请选择其他图片。", true); }
                if (_shutdown) break;
            }
            if (!_shutdown) { RenderAttachments(); if (!failed) SetStatus($"已添加 {added} 张图片。PNG/JPEG/BMP/GIF 第一帧，长边缩至 1280；原图保持不变。"); }
        }
        finally { _preparingImage = false; if (!_shutdown) { RefreshControls(); RefreshCharacter(); } }
    }

    private async Task PasteImageAsync()
    {
        if (_generating || _switching || _preparingImage || _shutdown || _configuring || _sessionSwitching || VoiceInputBusy || ModelMemoryBusy) return;
        if (!CanAttachImages()) return;
        if (_pendingImages.Count >= OllamaChatImage.MaxFiles) { SetStatus("每次最多发送 2 张图片。", true); return; }
        _preparingImage = true; RefreshControls();
        try
        {
            var bitmap = Clipboard.GetImage();
            if (bitmap == null) { SetStatus("剪贴板中没有图片。可复制图片后粘贴，或使用“添加图片”。"); return; }
            SetStatus("正在本机处理剪贴板图片…");
            var image = await OllamaChatImage.FromClipboardAsync(bitmap, _lifetime.Token);
            if (!_shutdown) { _pendingImages.Add(image); RenderAttachments(); SetStatus("已添加剪贴板图片，可输入问题后发送。"); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_shutdown) SetStatus(ex is InvalidDataException ? ex.Message : "无法读取剪贴板图片，请使用“添加图片”。", true); }
        finally { _preparingImage = false; if (!_shutdown) { RefreshControls(); RefreshCharacter(); } }
    }

    private void RenderAttachments()
    {
        _attachments.Children.Clear();
        _attachments.Margin = new Thickness(0, 0, 0, _pendingImages.Count == 0 ? 0 : 6);
        foreach (var attachment in _pendingImages.ToArray())
        {
            var card = new StackPanel { Width = 76 };
            card.Children.Add(new Image { Source = attachment.Preview, Height = _compactLayout ? 30 : 46, Stretch = Stretch.Uniform, ToolTip = attachment.Name });
            var remove = Ui.Button("移除", (_, _) => { _pendingImages.Remove(attachment); RenderAttachments(); RefreshControls(); }); remove.Padding = new Thickness(5, 2, 5, 2); remove.FontSize = 10; remove.Margin = new Thickness(0, 2, 0, 0); AutomationProperties.SetName(remove, "移除图片 " + attachment.Name); card.Children.Add(remove);
            _attachments.Children.Add(new Border { Child = card, Padding = new Thickness(3), Margin = new Thickness(0, 0, 5, 0), Background = Ui.Brush("#E6EEDF"), CornerRadius = new CornerRadius(8) });
        }
        RefreshControls();
        QueueDraftSave();
    }

    private bool CanAttachImages()
    {
        if (!_connected || _installedModels.Count == 0) { SetStatus("请先连接 Ollama 并选择已安装模型。", true); return false; }
        if (_capabilities.SupportsChat == false) { SetStatus("当前模型不支持聊天，请选择其他模型。", true); return false; }
        if (_capabilities.SupportsImages == false) { SetStatus("当前模型仅支持文字；请选择支持图片的模型。", true); return false; }
        return true;
    }

    private async void InputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.ImeProcessed && e.ImeProcessedKey == Key.Escape)) _imeComposing = false;
        if (_imeComposing || e.Key == Key.ImeProcessed) return;
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        { e.Handled = true; await SendAsync(); }
    }

    private void WindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // This tunnelling handler runs before the input clears its composition flag.
        // Leave IME Escape untouched so it can dismiss candidates without hiding the panel.
        if (e.Handled || e.Key != Key.Escape || _imeComposing || _models.IsDropDownOpen) return;
        e.Handled = true;
        if (VoiceInputBusy) { CancelVoiceInput(); return; }
        if (ModelMemoryBusy) { CancelModelMemoryOperation(true); return; }
        Close();
    }

    private async void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(DataFormats.Bitmap))
        { e.CancelCommand(); await PasteImageAsync(); }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = !_generating && !_switching && !_preparingImage && !_configuring && !_sessionSwitching && !VoiceInputBusy && _connected && _installedModels.Count > 0 && _capabilities.SupportsChat != false && _capabilities.SupportsImages != false ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        { e.Handled = true; await AddImageFilesAsync(paths); }
    }
}
