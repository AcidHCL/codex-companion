using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

internal sealed partial class PetWindow : Window
{
    public event Action? OpenSettingsRequested;
    public event Action? OpenDashboardRequested;
    public event Action? OpenChatRequested;
    public event Action? OpenQuickActionsRequested;
    public event Action? OpenLive2DRequested;
    public event Action? ExitRequested;
    public event Action? SettingsChanged;
    private readonly IReadOnlyList<SkinInfo> _skins;
    private readonly bool _persist;
    private readonly Func<MotionPreferences> _motionPreferences;
    private readonly Func<WindowsSessionSnapshot>? _querySession;
    private readonly bool _subscribeToSystemEvents;
    private readonly Func<IntPtr, DesktopPlatformSnapshot>? _movementPlatformProvider;
    private readonly Image _image;
    private readonly Grid _character;
    private Live2DView? _live2D;
    private int _modelGeneration;
    private bool _closed;
    public Live2DModelInfo? CurrentLive2DModel { get; private set; }
    public string CurrentDisplayName => CurrentLive2DModel?.Name ?? CurrentSkin.Name;
    public string Live2DStatus { get; private set; } = L.T("选择或导入 Live2D 模型", "Select or import a Live2D model");
    public bool IsLive2DLoading { get; private set; }
    internal Live2DView? Live2DRenderer => _live2D;
    private readonly TextBlock _speech;
    private readonly Border _bubble;
    private readonly TextBlock _stateLabel;
    private readonly Border _stateBadge;
    private readonly ScaleTransform _breath = new(1, 1);
    private readonly TranslateTransform _bounce = new();
    private readonly DispatcherTimer _saveTimer;
    private readonly DispatcherTimer _speechTimer;
    private bool _restoring = true;
    private IntPtr _handle;
    private bool _trayMenuOpen;
    internal TopmostController? TopmostGuard { get; private set; }
    public DesktopSettings Settings { get; }
    public SkinInfo CurrentSkin { get; private set; }

    public PetWindow(IReadOnlyList<SkinInfo> skins, DesktopSettings settings, bool persist,
        Func<MotionPreferences>? motionPreferences = null, Func<WindowsSessionSnapshot>? querySession = null,
        bool subscribeToSystemEvents = true, Func<IntPtr, DesktopPlatformSnapshot>? movementPlatformProvider = null)
    {
        _skins = skins;
        Settings = settings;
        _persist = persist;
        _querySession = querySession;
        _subscribeToSystemEvents = subscribeToSystemEvents;
        _motionPreferences = motionPreferences ?? (() => MotionPreferences.Capture(_querySession?.Invoke() ?? WindowsSessionState.QueryCurrent()));
        _movementPlatformProvider = movementPlatformProvider;
        CurrentSkin = skins.FirstOrDefault(s => s.Id == settings.SkinId) ?? skins[0];
        Settings.SkinId = CurrentSkin.Id;
        Title = AppVersion.WindowTitle(CurrentSkin.Name);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = Settings.Topmost;
        Width = Settings.Size + 24;
        Height = Settings.Size + 88;
        var root = new Grid { Background = Brushes.Transparent };
        _character = new Grid { Margin = new Thickness(12, 46, 12, 36), Background = Brushes.Transparent, Cursor = Cursors.Hand };
        _image = new Image
        {
            Source = LoadDefaultSkinImage(CurrentSkin),
            Stretch = Stretch.Uniform,
            Margin = new Thickness(0),
            Cursor = Cursors.Hand,
            RenderTransformOrigin = new Point(.5, 1)
        };
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var transforms = new TransformGroup();
        transforms.Children.Add(_breath);
        transforms.Children.Add(_bounce);
        _image.RenderTransform = transforms;
        _character.Children.Add(_image);
        _character.SizeChanged += (_, _) => QueueFootGeometryRefresh();
        root.Children.Add(_character);
        _speech = new TextBlock { FontSize = 12, Foreground = Ui.Brush("#EAF5EE"), FontFamily = new FontFamily("Microsoft YaHei UI"), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, MaxHeight = 62, TextTrimming = TextTrimming.CharacterEllipsis };
        _bubble = new Border
        {
            Background = Ui.Brush("#183F35"), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13, 8, 13, 8), Child = _speech,
            VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(8, 20, 8, 0), MaxWidth = 270, Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };
        root.Children.Add(_bubble);
        _stateLabel = Ui.Text(L.T("连接 Codex…", "Connect Codex…"), 10, "#EAF5EE");
        _stateLabel.TextTrimming = TextTrimming.CharacterEllipsis;
        _stateBadge = new Border { Child = _stateLabel, Background = Ui.Brush("#214F38"), CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(12, 0, 12, 2), Cursor = Cursors.Hand, ToolTip = AppVersion.ProductLabel + L.T("\n点击打开 Codex 工作看板", "\nClick to open the Codex dashboard") };
        _stateBadge.MouseLeftButtonUp += (_, e) => { OpenDashboardRequested?.Invoke(); e.Handled = true; };
        root.Children.Add(_stateBadge);
        var versionBadge = new Border
        {
            Child = Ui.VersionLabel(false, "#E1EDE5", 10), Background = Ui.Brush("#214F38"),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(5, 1, 5, 1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 12, 0), IsHitTestVisible = false
        };
        root.Children.Add(versionBadge);
        Content = root;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => { _saveTimer.Stop(); SaveNow(); };
        _speechTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.3) };
        _speechTimer.Tick += (_, _) => { _speechTimer.Stop(); _bubble.Visibility = Visibility.Collapsed; UpdateBadgePlacement(); };
        SourceInitialized += (_, _) =>
        {
            _handle = new WindowInteropHelper(this).Handle;
            NativeMethods.SetToolWindow(_handle);
            NativeMethods.SetNoActivate(_handle);
            NativeMethods.SetClickThrough(_handle, Settings.ClickThrough);
            HwndSource.FromHwnd(_handle)?.AddHook(WindowHook);
            TopmostGuard = new TopmostController(Dispatcher, _handle, () => Settings.Topmost,
                () => !_closed && IsVisible && !_displayRenderingPaused, () => _menuOpen || _trayMenuOpen);
            InitializeDisplayLifecycle();
        };
        Loaded += (_, _) =>
        {
            if (Settings.Left.HasValue && Settings.Top.HasValue)
            {
                NativeMethods.SetWindowPositionPixels(_handle, (int)Settings.Left.Value, (int)Settings.Top.Value);
                ClampPetToVisibleWorkArea();
            }
            else ResetPosition();
            _restoring = false;
            ApplyMotion();
            InitializeMovement();
            TopmostGuard?.Refresh();
            DisplayLifecycle?.SetHostVisible(IsVisible);
            if (GraphicsRecovery.CompatibilityMode) { SuspendDisplayRendering(); ResumeDisplayRendering(); }
            else if (!string.IsNullOrWhiteSpace(Settings.Live2DModelId) || !string.IsNullOrWhiteSpace(Settings.ScenePackId) ||
                BundledSceneAnimations.SupportsSkin(CurrentSkin.Id)) DisplayLifecycle?.RequestRecovery();
        };
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) { ResetClickInteraction(); InvalidateLlmBehavior(); }
            TopmostGuard?.Refresh();
            DisplayLifecycle?.SetHostVisible(IsVisible);
            UpdateActivityPause();
            if (!IsVisible && (IsSinging || IsManualSoundScene)) StopScenePreview();
            RefreshSpeechSuspension();
        };
        LocationChanged += (_, _) =>
        {
            if (_adjustingSceneLayout) return;
            if (_movement?.IsDragging == true) { _movement.ObserveDrag(); ObserveBehaviorDrag(); return; }
            if (!_restoring && _movement?.IsMovingWindow != true) QueueSave();
        };
        _character.MouseLeftButtonDown += OnLeftDown;
        _character.MouseMove += OnLeftMove;
        _character.MouseLeftButtonUp += OnLeftUp;
        _character.LostMouseCapture += OnPointerCaptureLost;
        _character.MouseWheel += (_, e) => { SetSize(Settings.Size + (e.Delta > 0 ? 20 : -20)); Say(L.T("大小 ", "Size ") + (int)Settings.Size + " px"); e.Handled = true; };
        _character.MouseRightButtonUp += (_, e) => { InvalidateLlmBehavior(); OpenQuickActionsRequested?.Invoke(); e.Handled = true; };
        _character.MouseDown += (_, e) => { if (e.ChangedButton == MouseButton.Middle) { SetMotion(!Settings.MotionEnabled); Say(Settings.MotionEnabled ? (CurrentLive2DModel != null ? L.T("眨眼与呼吸已开启", "Blinking and breathing enabled") : L.T("呼吸动画已开启", "Breathing animation enabled")) : L.T("安静陪伴中", "Keeping you quiet company")); e.Handled = true; } };
        Closing += (_, _) =>
        {
            ResetClickInteraction();
            InvalidateLlmBehavior();
            SaveNow();
            _closed = true;
            DisposeSpeech();
            DisplayLifecycle?.Dispose();
            TopmostGuard?.Dispose();
            _modelGeneration++;
            ReleaseScene();
            _movement?.Dispose();
            SystemParameters.StaticPropertyChanged -= OnSystemMotionChanged;
            ReleaseLive2D();
            _saveTimer.Stop(); _speechTimer.Stop();
            _footGeometryTimer?.Stop();
        };
        Closed += (_, _) => _handle = IntPtr.Zero;
        InitializeSpeech();
    }

    private IntPtr WindowHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        DisplayLifecycle?.HandleWindowMessage(msg, wParam, lParam);
        TopmostGuard?.OnWindowMessage(msg, lParam);
        if (msg == 0x0084) // WM_NCHITTEST: spare the taskbar under the transparent tail of the HWND.
        {
            long packed = lParam.ToInt64();
            int screenY = (short)((packed >> 16) & 0xffff);
            if (screenY > NativeMethods.GetWindowBoundsPixels(hwnd).Top + VisibleFootPixels.Y + 1)
            {
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            }
        }
        if (msg == 0x02E0) Dispatcher.BeginInvoke(() => // WM_DPICHANGED
        {
            if (_closed || DisplayRenderingPaused) return;
            UpdateLayout();
            if (Settings.MovementEnabled && _movement is { IsDragging: false })
                _movement.ReconfigureGeometry(); // Retain support and screen-space feet across monitor scaling.
            else ClampPosition();
            QueueFootGeometryRefresh(); QueueSave();
        });
        return IntPtr.Zero;
    }

    internal void OpenMoreActions()
    {
        var menu = new ContextMenu { Background = Ui.Brush("#FAFCF7"), Foreground = Ui.Brush("#284B37"),
            BorderBrush = Ui.Brush("#CFDFCA"), BorderThickness = new Thickness(1), Padding = new Thickness(6),
            FontFamily = new FontFamily("Microsoft YaHei UI"), FontSize = 12 };
        menu.Resources[SystemColors.MenuBrushKey] = Ui.Brush("#FAFCF7");
        menu.Resources[SystemColors.HighlightBrushKey] = Ui.Brush("#E1EDD9");
        menu.Resources[SystemColors.HighlightTextBrushKey] = Ui.Brush("#284B37");
        var title = new MenuItem { Header = CurrentDisplayName + " · " + AppVersion.ProductLabel, IsEnabled = false };
        menu.Items.Add(title);
        menu.Items.Add(new Separator());
        AddMenu(menu, L.T("和桌宠聊天…", "Chat with your pet…"), () => OpenChatRequested?.Invoke());
        var skinsMenu = new MenuItem { Header = L.T("换个角色", "Change character") };
        foreach (var skin in _skins)
        {
            var item = new MenuItem { Header = skin.Name, IsCheckable = true, IsChecked = CurrentLive2DModel == null && skin.Id == CurrentSkin.Id };
            item.Click += (_, _) => { SelectSkin(skin.Id); Say(L.T("现在是 ", "Now using ") + skin.Name); };
            skinsMenu.Items.Add(item);
        }
        menu.Items.Add(skinsMenu);
        AddMenu(menu, L.T("Live2D 模型…", "Live2D models…"), OpenLive2DLibrary);
        AddMenu(menu, L.T("场景动画与导入…", "Scene animations and import…"), OpenSceneLibrary);
        var sing = new MenuItem { Header = L.T("唱 Let Me Go", "Sing Let Me Go"), IsEnabled = CanSing && !IsSinging };
        sing.Click += (_, _) => ObserveRendererOperation(SingAsync()); menu.Items.Add(sing);
        var stopSong = new MenuItem { Header = L.T("停止唱歌", "Stop singing"), IsEnabled = IsSinging };
        stopSong.Click += (_, _) => StopSinging(); menu.Items.Add(stopSong);
        var emotions = new MenuItem { Header = L.T("表演与情绪", "Performances & emotions") };
        foreach (string action in PetPerformanceCatalog.Installed(CurrentScenePack).Where(action => action != "sing"))
        {
            var expression = new MenuItem { Header = PetPerformanceCatalog.Label(action), IsEnabled = CanPlayPerformance(action) };
            expression.Click += (_, _) => ObserveRendererOperation(PreviewSceneAsync(action)); emotions.Items.Add(expression);
        }
        emotions.IsEnabled = emotions.Items.Count > 0;
        menu.Items.Add(emotions);
        var sound = new MenuItem { Header = L.T("音乐与声音", "Music & sound"), IsCheckable = true, IsChecked = Settings.SoundEnabled };
        sound.Click += (_, _) => SetSoundEnabled(sound.IsChecked); menu.Items.Add(sound);
        var movement = new MenuItem { Header = L.T("桌面行走与窗口互动", "Desktop movement & window interaction"), IsCheckable = true, IsChecked = Settings.MovementEnabled };
        movement.Click += (_, _) => SetMovement(movement.IsChecked);
        menu.Items.Add(movement);
        AddMenu(menu, L.T("跳到附近窗口", "Jump to a nearby window"), JumpToNearbyWindow);
        if (UsesBuiltInScenes)
        {
            AddMenu(menu, L.T("向左侧窗沿攀爬", "Climb the window edge on the left"), () => ClimbNearbyWindow(-1));
            AddMenu(menu, L.T("向右侧窗沿攀爬", "Climb the window edge on the right"), () => ClimbNearbyWindow(1));
        }
        AddMenu(menu, L.T("Codex 工作看板…", "Codex dashboard…"), () => OpenDashboardRequested?.Invoke());
        AddMenu(menu, L.T("设置…", "Settings…"), () => OpenSettingsRequested?.Invoke());
        AddMenu(menu, L.T("放大一点", "Make larger"), () => SetSize(Settings.Size + 20));
        AddMenu(menu, L.T("缩小一点", "Make smaller"), () => SetSize(Settings.Size - 20));
        var top = new MenuItem { Header = L.T("始终置顶", "Always on top"), IsCheckable = true, IsChecked = Settings.Topmost };
        top.Click += (_, _) => SetTopmost(top.IsChecked);
        menu.Items.Add(top);
        var motion = new MenuItem { Header = CurrentLive2DModel != null ? L.T("眨眼与呼吸", "Blinking & breathing") : L.T("呼吸动画", "Breathing animation"), IsCheckable = true, IsChecked = Settings.MotionEnabled };
        motion.Click += (_, _) => SetMotion(motion.IsChecked);
        menu.Items.Add(motion);
        AddMenu(menu, L.T("开启鼠标穿透", "Enable click-through"), () => { SetClickThrough(true); Say(L.T("从托盘打开设置可恢复互动", "Open Settings from the tray to restore interaction")); });
        menu.Items.Add(new Separator());
        AddMenu(menu, L.T("暂时隐藏（托盘恢复）", "Hide temporarily (restore from tray)"), Hide);
        AddMenu(menu, L.T("退出桌宠", "Quit AI Pet"), () => ExitRequested?.Invoke());
        menu.PlacementTarget = _character;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;
        _menuOpen = true;
        UpdateActivityPause();
        TopmostGuard?.RequestRestore();
        menu.Closed += (_, _) => { _menuOpen = false; UpdateActivityPause(); TopmostGuard?.RequestRestore(); };
        menu.IsOpen = true;
    }

    private static void AddMenu(ContextMenu menu, string label, Action action)
    {
        var item = new MenuItem { Header = label };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    public void SelectSkin(string id)
    {
        var skin = _skins.FirstOrDefault(s => s.Id == id);
        if (skin == null) return;
        ResetClickInteraction();
        InvalidateLlmBehavior();
        DisplayLifecycle?.ResetRendererFailureBudget();
        if (CurrentSkin.Id != id) ForgetDisplaySelectionsForSkinChange();
        StopSceneForBaseChange(id);
        _modelGeneration++;
        ReleaseLive2D();
        CurrentLive2DModel = null;
        IsLive2DLoading = false;
        Settings.Live2DModelId = null;
        _image.Visibility = Visibility.Visible;
        CurrentSkin = skin;
        Settings.SkinId = id;
        StopSpeech();
        _voice?.ApplySettings();
        RefreshSpeechSuspension();
        Live2DStatus = L.T("当前使用 PNG：", "Current PNG skin: ") + skin.Name;
        _image.Source = LoadDefaultSkinImage(skin);
        _live2DGeometry = null;
        Title = AppVersion.WindowTitle(skin.Name);
        ApplyMotion();
        Bounce();
        Changed();
        ObserveRendererOperation(RestoreDefaultSceneAsync());
        if (_displayRenderingPaused && !GraphicsRecovery.CompatibilityMode) DisplayLifecycle?.RequestRecovery(userInitiated: true);
    }

    private static BitmapImage LoadDefaultSkinImage(SkinInfo skin)
    {
        if (BundledSceneAnimations.SupportsSkin(skin.Id))
        {
            string folder = skin.Id == "gpt" ? "gpt-small" : "deepseek-small";
            string directory = Path.Combine(AppContext.BaseDirectory, "assets", "scenes", folder, "frames", "idle");
            foreach (string name in new[] { "01.png", "001.png" })
            {
                string idle = Path.Combine(directory, name);
                if (File.Exists(idle)) return AssetLoader.Load(idle);
            }
        }
        return AssetLoader.Load(skin.ImagePath);
    }

    public void OpenLive2DLibrary() => OpenLive2DRequested?.Invoke();

    private async Task RestoreLive2DAsync()
    {
        var models = Live2DLibrary.Load(out var issues);
        var model = models.FirstOrDefault(m => m.Id == Settings.Live2DModelId);
        if (model != null) await SelectLive2DAsync(model);
        else
        {
            Settings.Live2DModelId = null;
            Live2DStatus = issues.Count > 0 ? issues[0] : L.T("原 Live2D 模型不可用，已显示 PNG 皮肤。", "The previous Live2D model is unavailable. Showing the PNG skin.");
            Changed();
            Say(L.T("Live2D 模型不可用，可从设置中重新导入。", "The Live2D model is unavailable. Import it again in Settings."));
        }
    }

    public async Task<bool> SelectLive2DAsync(Live2DModelInfo model)
    {
        if (_closed) return false;
        InvalidateLlmBehavior();
        if (!_displayRestoreInProgress) DisplayLifecycle?.ResetRendererFailureBudget();
        if (DeferModelSelection(model)) return true;
        if (UsesBuiltInScenes || (IsSceneLoading && BundledSceneAnimations.SupportsSkin(CurrentSkin.Id)))
        { ReleaseScene(); Settings.ScenePackId = null; }
        else StopScenePreview();
        int generation = ++_modelGeneration;
        ReleaseLive2D();
        CurrentLive2DModel = null;
        _image.Visibility = Visibility.Visible;
        IsLive2DLoading = true;
        Live2DStatus = L.T("正在加载 ", "Loading ") + model.Name + "…";
        SettingsChanged?.Invoke();
        Live2DView? renderer = null;
        try
        {
            renderer = new Live2DView(model, RendererProfileDirectory);
            renderer.SetPaused(!IsVisible || DisplayRenderingPaused);
            renderer.SetMotionEnabled(Settings.MotionEnabled);
            _live2D = renderer;
            _character.Children.Insert(0, renderer);
            UpdateLayout();
            await renderer.LoadAsync(model);
            if (_closed || generation != _modelGeneration) return false;
            CharacterGeometry? loadedGeometry = null;
            if (IsVisible)
            {
                try { loadedGeometry = await renderer.CaptureContactGeometryAsync(); }
                catch (Exception ex) when (!GraphicsFailure.IsFatal(ex)) { Program.Log(ex); } // Measurement is optional; fatal WPF is not.
            }
            if (_closed || generation != _modelGeneration) return false;
            _live2DGeometry = loadedGeometry;
            CurrentLive2DModel = model;
            Settings.Live2DModelId = model.Id;
            _image.Visibility = Visibility.Hidden;
            Title = AppVersion.WindowTitle(model.Name + " · Live2D");
            Live2DStatus = L.T("已载入 · ", "Loaded · ") + model.Name;
            IsLive2DLoading = false;
            renderer.Failed += error =>
            {
                if (_closed || generation != _modelGeneration) return;
                Program.Log(new InvalidOperationException(error));
                QueueRendererRecovery(L.T("Live2D 暂不可用", "Live2D temporarily unavailable"));
            };
            ApplyMotion();
            QueueFootGeometryRefresh();
            Changed();
            return true;
        }
        catch (Exception ex) when (!GraphicsFailure.IsFatal(ex))
        {
            if (_closed || generation != _modelGeneration) return false;
            Program.Log(ex);
            ReleaseLive2D();
            CurrentLive2DModel = null;
            IsLive2DLoading = false;
            Live2DStatus = L.T("加载失败：", "Load failed: ") + ex.Message;
            Title = AppVersion.WindowTitle(CurrentSkin.Name);
            _image.Visibility = Visibility.Visible;
            ApplyMotion();
            Changed();
            if (!_displayRestoreInProgress) QueueRendererRecovery(L.T("模型载入暂时失败", "Model loading temporarily failed"));
            return false;
        }
    }

    private void ReleaseLive2D()
    {
        _live2DGeometry = null;
        if (_live2D == null) return;
        var previous = _live2D;
        _live2D = null;
        _character.Children.Remove(previous);
        previous.Dispose();
    }

    public void SetSize(double value)
    {
        value = Math.Clamp(Math.Round(value / 10) * 10, 160, 440);
        if (Math.Abs(value - Settings.Size) < .01) return;
        InvalidateLlmBehavior();
        var oldBounds = _handle != IntPtr.Zero ? NativeMethods.GetWindowBoundsPixels(_handle) : Rect.Empty;
        var oldFoot = VisibleFootPixels;
        Settings.Size = value;
        Width = value + 24;
        ApplySceneThoughtLayout(_sceneThoughts, preserveFoot: false);
        UpdateLayout();
        if (_handle != IntPtr.Zero && !oldBounds.IsEmpty)
        {
            var newBounds = NativeMethods.GetWindowBoundsPixels(_handle);
            var newFoot = VisibleFootPixels;
            NativeMethods.SetWindowPositionPixels(_handle, (int)Math.Round(oldBounds.Left + oldFoot.X - newFoot.X), (int)Math.Round(oldBounds.Top + oldFoot.Y - newFoot.Y));
            ClampPetToVisibleWorkArea();
        }
        _movement?.NotifyExternalMove();
        Changed();
    }

    public void SetLanguage(string value)
    {
        if (value is not (L.Chinese or L.English))
            throw new ArgumentException(L.T("不支持的界面语言。", "Unsupported interface language."), nameof(value));
        string previous = Settings.Language;
        Settings.Language = value;
        try { if (_persist) Settings.Save(); }
        catch { Settings.Language = previous; throw; }
        // Apply at next startup. Do not interrupt active chat, audio or drafts.
        SettingsChanged?.Invoke();
    }

    public void SetTopmost(bool value) { Settings.Topmost = value; Topmost = value; TopmostGuard?.Refresh(); Changed(); }
    internal void SetTrayMenuOpen(bool value) { _trayMenuOpen = value; TopmostGuard?.RequestRestore(); }
    public void SetMotion(bool value) { if (Settings.MotionEnabled != value) InvalidateLlmBehavior(); Settings.MotionEnabled = value; ApplyMotion(); Changed(); }
    public void SetClickThrough(bool value) { if (value) ResetClickInteraction(); Settings.ClickThrough = value; if (_handle != IntPtr.Zero) NativeMethods.SetClickThrough(_handle, value); Changed(); }

    public void ClampPosition() { if (_handle != IntPtr.Zero && (!DisplayRenderingPaused || _displayRestoreInProgress)) { ClampPetToVisibleWorkArea(); _movement?.NotifyExternalMove(); QueueFootGeometryRefresh(); } }

    public void ResetPosition()
    {
        if (_handle == IntPtr.Zero) return;
        InvalidateLlmBehavior();
        var work = NativeMethods.GetWindowWorkAreaPixels(_handle);
        var bounds = NativeMethods.GetWindowBoundsPixels(_handle);
        double scale = NativeMethods.GetDpiScale(_handle);
        var visible = CharacterVisibleBoundsPixels;
        var foot = VisibleFootPixels;
        NativeMethods.SetWindowPositionPixels(_handle, (int)Math.Round(work.Right - visible.Right - 30 * scale), (int)Math.Round(work.Bottom - foot.Y));
        ClampPetToVisibleWorkArea();
        _movement?.NotifyExternalMove();
        QueueSave();
    }

    private void ApplyMotion()
    {
        if (_closed) return;
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _breath.ScaleY = 1;
        UpdatePngFootPivot();
        UpdateBadgePlacement();
        UpdateActivityPause();
        UpdateSceneVisibility();
        _live2D?.SetMotionEnabled(Settings.MotionEnabled);
        if (DisplayRenderingPaused || !Settings.MotionEnabled || !_motionPreferences().AllowsAnimation || !IsVisible || CurrentLive2DModel != null || _sceneActive) return;
        var animation = new DoubleAnimation(1, 1.008, TimeSpan.FromSeconds(1.7))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        _breath.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    public void Bounce()
    {
        if (DisplayRenderingPaused || CurrentLive2DModel != null || _sceneActive || Settings.MovementEnabled) return;
        var animation = new DoubleAnimationUsingKeyFrames();
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        animation.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(360)), new BounceEase { Bounces = 1, Bounciness = 2 }));
        animation.FillBehavior = FillBehavior.Stop;
        Timeline.SetDesiredFrameRate(animation, 30);
        _bounce.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void ShowSpeechBubble(string text) { _speech.Text = text; _bubble.Visibility = Visibility.Visible; UpdateBadgePlacement(); _speechTimer.Stop(); _speechTimer.Start(); }
    public void Say(string text) { ShowSpeechBubble(text); SpeakText(text, PetSpeechSource.Preset); }
    private void Changed() { QueueSave(); SettingsChanged?.Invoke(); }
    private void QueueSave() { if (!_persist || _restoring) return; _saveTimer.Stop(); _saveTimer.Start(); }
    public void SaveNow()
    {
        if (!_persist || _restoring) return;
        try
        {
            if (_handle != IntPtr.Zero && !DisplayRenderingPaused)
            {
                var bounds = NativeMethods.GetWindowBoundsPixels(_handle);
                Settings.Left = bounds.Left;
                // Persist the ordinary body window position, not temporary
                // headroom used by an explicitly selected singing action.
                Settings.Top = bounds.Top + _sceneHeadroom * NativeMethods.GetDpiScale(_handle);
                Settings.DpiMonitor = NativeMethods.GetMonitorName(_handle);
            }
            Settings.Save();
        }
        catch (Exception ex) { Program.Log(ex); }
    }

    public void SetCodexStatus(CodexSnapshot snapshot)
    {
        // Keep the primary character Codex-only even if a caller supplies the
        // dashboard's unfiltered snapshot instead of the router projection.
        snapshot = TaskSourcePolicy.Project(snapshot, TaskSource.Codex);
        UpdateBadgePlacement();
        int running = snapshot.Tasks.Count(t => t.IsRunning);
        int attention = snapshot.Tasks.Count(t => t.NeedsAttention);
        var codex = snapshot.Buckets.FirstOrDefault(b => b.Id == "codex") ?? snapshot.Buckets.FirstOrDefault();
        var week = codex?.Windows.FirstOrDefault(w => w.DurationMinutes == 10080);
        string usage = week?.RemainingPercent is double remaining ? L.T(" · 周余 ", " · Weekly remaining ") + remaining.ToString("0.#") + "%" : "";
        if (snapshot.UsageUpdatedAt is DateTimeOffset at && (DateTimeOffset.Now - at).TotalSeconds > 90) usage = L.T(" · 用量待更新", " · Usage pending");
        _stateLabel.Text = !snapshot.Connected ? (snapshot.Refreshing ? L.T("连接 Codex…", "Connect Codex…") : L.T("Codex 未连接", "Codex disconnected")) : (attention > 0 ? attention + L.T(" 项待处理", " need attention") : running > 0 ? running + L.T(" 项执行中", " running") : L.T("Codex 待命", "Codex standing by")) + usage;
        _stateBadge.Background = Ui.Brush(!snapshot.Connected ? "#69736A" : attention > 0 ? "#876344" : running > 0 ? "#325C76" : "#214F38");
        _stateBadge.ToolTip = AppVersion.ProductLabel + "\n" + snapshot.StatusText + L.T("\n点击查看用量、任务与对话", "\nClick for usage, tasks, and conversations");
        UpdateSceneFromCodex(snapshot);
    }
    public new void Hide() { base.Hide(); ApplyMotion(); SettingsChanged?.Invoke(); }
    public new void Show() { base.Show(); TopmostGuard?.Refresh(); ApplyMotion(); QueueFootGeometryRefresh(); SettingsChanged?.Invoke(); }
    public void SavePreview(string path)
    {
        UpdateLayout();
        var target = new RenderTargetBitmap((int)Math.Ceiling(ActualWidth), (int)Math.Ceiling(ActualHeight), 96, 96, PixelFormats.Pbgra32);
        target.Render((Visual)Content);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(target));
        using var stream = File.Create(path); png.Save(stream);
    }
}
