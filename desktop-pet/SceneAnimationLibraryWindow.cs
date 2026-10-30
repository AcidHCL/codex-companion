using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPet;

internal static class SceneAnimationLibrary
{
    public static IReadOnlyList<SceneAnimationPack> Load(out IReadOnlyList<string> issues)
    {
        var catalog = new SceneAnimationCatalog();
        var result = new List<SceneAnimationPack>(catalog.Load(out var importedIssues));
        var errors = new List<string>(importedIssues);
        foreach (var (skin, id) in new[] { ("gpt", BundledSceneAnimations.GptId),
            ("deepseek", BundledSceneAnimations.DeepSeekId), ("deepseek", BundledSceneAnimations.DeepSeekRedrawnId) })
        {
            try
            {
                if (BundledSceneAnimations.FindForSkin(skin, id) is { } builtIn) result.Insert(0, builtIn);
            }
            catch (Exception ex) { errors.Add("内置动画（" + id + "）：" + ex.Message); }
        }
        string samples = Path.Combine(AppContext.BaseDirectory, "assets", "scene-samples");
        if (Directory.Exists(samples))
        {
            foreach (string file in Directory.EnumerateFiles(samples, "*.petanim.json", SearchOption.AllDirectories).Take(64))
            {
                try
                {
                    string name = Path.GetFileName(file)[..^".petanim.json".Length];
                    string id = "sample-scene-" + string.Concat(name.Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'));
                    result.Add(catalog.Validate(file, id));
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(file) + "：" + ex.Message); }
            }
        }
        issues = errors.AsReadOnly();
        return result.AsReadOnly();
    }
}

internal sealed class SceneAnimationLibraryWindow : Window
{
    private readonly PetWindow _pet;
    private readonly ListBox _packs;
    private readonly ComboBox _actions;
    private readonly TextBlock _status, _details;
    private readonly Button _import, _select, _preview, _stop, _clear, _redrawn, _classic;
    private readonly CheckBox _automatic;
    private bool _working, _syncing;

    public SceneAnimationLibraryWindow(PetWindow pet)
    {
        _pet = pet;
        Title = AppVersion.WindowTitle("场景动画");
        Width = 720; Height = 780; MinWidth = 650; MinHeight = 730;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("#F4F6F0");
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        var version = Ui.VersionLabel(); version.Margin = new Thickness(0, 0, 0, 7); header.Children.Add(version);
        header.Children.Add(Ui.Text("场景动画", 26, "#203E2C", true));
        var intro = Ui.Text("DeepSeek 可切换新旧动画；GPT 小龙娘使用经典模式的状态与表情动画。\n选择会自动保存；动作随任务、互动和移动状态播放。", 13, "#66815C");
        intro.TextWrapping = TextWrapping.Wrap; intro.LineHeight = 22; intro.Margin = new Thickness(0, 10, 0, 16);
        header.Children.Add(intro); root.Children.Add(header);
        _packs = new ListBox { DisplayMemberPath = "Name", BorderBrush = Ui.Brush("#D3DFCF"), BorderThickness = new Thickness(1), Padding = new Thickness(8), FontSize = 15 };
        Grid.SetRow(_packs, 1); root.Children.Add(_packs);

        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        _details = Ui.Text("", 12, "#597D61"); _details.TextWrapping = TextWrapping.Wrap; _details.LineHeight = 20;
        footer.Children.Add(_details);
        _automatic = new CheckBox { Content = "按任务和移动状态自动播放动作", Margin = new Thickness(0, 10, 0, 10) };
        _automatic.Checked += (_, _) => { if (!_syncing) _pet.SetSceneAuto(true); };
        _automatic.Unchecked += (_, _) => { if (!_syncing) _pet.SetSceneAuto(false); };
        footer.Children.Add(_automatic);
        var previewRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        _actions = new ComboBox { Width = 195, MinHeight = 35, DisplayMemberPath = "Label", VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(8, 3, 8, 3) };
        previewRow.Children.Add(_actions);
        _preview = Ui.Button("预览动作", async (_, _) => await PreviewAsync()); _preview.Margin = new Thickness(10, 0, 0, 0);
        _stop = Ui.Button("结束预览并恢复状态", (_, _) => { _pet.StopScenePreview(); RefreshState(); }); _stop.Margin = new Thickness(10, 0, 0, 0);
        previewRow.Children.Add(_preview); previewRow.Children.Add(_stop); footer.Children.Add(previewRow);
        _status = Ui.Text("", 12, "#597D61"); _status.TextWrapping = TextWrapping.Wrap; _status.MaxHeight = 80; _status.Margin = new Thickness(0, 0, 0, 12); footer.Children.Add(_status);
        var buttons = new WrapPanel();
        _import = Ui.Button("导入动画包…", async (_, _) => await ImportAsync());
        _select = Ui.Button("使用选中动画包", async (_, _) => await SelectAsync()); _select.Margin = new Thickness(10, 0, 0, 0);
        _clear = Ui.Button("恢复角色默认状态", (_, _) => { _pet.ClearScenePack(); RefreshState(); }); _clear.Margin = new Thickness(10, 0, 0, 0);
        buttons.Children.Add(_import); buttons.Children.Add(_select); buttons.Children.Add(_clear); footer.Children.Add(buttons);
        var versions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _redrawn = Ui.Button("切换 24 FPS 新版", async (_, _) => await SelectBuiltInAsync(BundledSceneAnimations.DeepSeekRedrawnId));
        _classic = Ui.Button("回到旧版动画", async (_, _) => await SelectBuiltInAsync(BundledSceneAnimations.DeepSeekId));
        _classic.Margin = new Thickness(10, 0, 0, 0);
        versions.Children.Add(_redrawn); versions.Children.Add(_classic); footer.Children.Add(versions);
        var help = Ui.Text("这里预览动作画面；实际跳跃、攀爬请用移动控制或向模型发出请求。情绪播放一次后恢复状态。可用表演取决于角色已安装的动作，GPT 暂无唱歌等独立表演。支持 PNG 序列、APNG、精灵图和视频；导入请选择 .petanim.json 清单。", 11, "#7B8D78");
        help.TextWrapping = TextWrapping.Wrap; help.LineHeight = 18; help.Margin = new Thickness(0, 12, 0, 0); footer.Children.Add(help);
        Grid.SetRow(footer, 2); root.Children.Add(footer); Content = root;
        _packs.SelectionChanged += (_, _) => RefreshSelection();
        _packs.MouseDoubleClick += async (_, _) => await SelectAsync();
        _actions.SelectionChanged += (_, _) => RefreshButtons();
        _pet.SettingsChanged += OnPetChanged;
        Closed += (_, _) => _pet.SettingsChanged -= OnPetChanged;
        RefreshList();
    }

    private void RefreshList(string? selectedId = null)
    {
        var packs = SceneAnimationLibrary.Load(out var issues);
        _packs.ItemsSource = packs;
        _packs.SelectedItem = packs.FirstOrDefault(pack => pack.Id == (selectedId ?? _pet.CurrentScenePack?.Id)) ??
            packs.FirstOrDefault(pack => MatchesSkin(pack)) ?? packs.FirstOrDefault();
        RefreshSelection();
        _status.Text = issues.Count > 0 ? issues[0] : packs.Count == 0 ? "还没有动画包。导入后可以在桌宠上预览。" : _pet.SceneStatus;
    }

    private bool MatchesSkin(SceneAnimationPack pack) => string.Equals(pack.SkinId, _pet.CurrentSkin.Id, StringComparison.OrdinalIgnoreCase);
    private void RefreshSelection()
    {
        string? oldAction = (_actions.SelectedItem as ActionChoice)?.Id;
        if (_packs.SelectedItem is SceneAnimationPack pack)
        {
            _details.Text = (MatchesSkin(pack) ? "适配当前角色：" + _pet.CurrentSkin.Name : "此包适配 " + pack.SkinId + "，当前角色为 " + _pet.CurrentSkin.Name + "（" + _pet.CurrentSkin.Id + "）。请先切换到匹配角色。") +
                "\n动作：" + string.Join("、", pack.Actions.Keys.Select(ActionLabel));
            var choices = pack.Actions.Keys.Select(id => new ActionChoice(id, ActionLabel(id))).ToArray();
            _actions.ItemsSource = choices;
            _actions.SelectedItem = choices.FirstOrDefault(item => item.Id == oldAction) ?? choices.FirstOrDefault();
        }
        else { _details.Text = "当前角色：" + _pet.CurrentSkin.Name; _actions.ItemsSource = null; }
        RefreshState();
    }

    private void RefreshState()
    {
        _syncing = true;
        _automatic.IsChecked = BundledSceneAnimations.IsBuiltIn(_pet.CurrentScenePack?.Id) || _pet.Settings.SceneAutoEnabled;
        _syncing = false;
        _status.Text = _pet.SceneStatus;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        bool ready = !_working && !_pet.IsSceneLoading;
        bool compatible = _packs.SelectedItem is SceneAnimationPack pack && MatchesSkin(pack);
        _import.IsEnabled = !_working;
        _select.IsEnabled = ready && compatible;
        _preview.IsEnabled = ready && compatible && _actions.SelectedItem is ActionChoice;
        _stop.IsEnabled = _pet.IsScenePlaying;
        bool builtIn = BundledSceneAnimations.IsBuiltIn(_pet.CurrentScenePack?.Id);
        _clear.IsEnabled = ready && _pet.CurrentScenePack != null && !builtIn;
        _automatic.IsEnabled = ready && !builtIn;
        _redrawn.IsEnabled = ready && _pet.CurrentSkin.Id == "deepseek" && _pet.CurrentScenePack?.Id != BundledSceneAnimations.DeepSeekRedrawnId;
        _classic.IsEnabled = ready && _pet.CurrentSkin.Id == "deepseek" && _pet.CurrentScenePack?.Id != BundledSceneAnimations.DeepSeekId;
    }

    private async Task SelectBuiltInAsync(string id)
    {
        if (_working || _pet.IsSceneLoading || _pet.CurrentSkin.Id != "deepseek") return;
        _packs.SelectedItem = _packs.Items.Cast<SceneAnimationPack>().FirstOrDefault(pack => pack.Id == id);
        await SelectAsync();
    }

    private void OnPetChanged()
    {
        // Skin changes may invalidate the selected package; re-evaluate compatibility immediately.
        RefreshSelection();
    }

    private async Task ImportAsync()
    {
        if (_working) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = AppVersion.WindowTitle("选择场景动画清单"), Filter = "桌宠动画包 (*.petanim.json)|*.petanim.json", CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        _working = true; RefreshButtons(); _status.Text = "正在检查并复制动画资源…";
        try
        {
            var pack = await new SceneAnimationCatalog().ImportAsync(picker.FileName);
            RefreshList(pack.Id);
            _status.Text = "已导入 " + pack.Name + (MatchesSkin(pack) ? "，可以选择使用或预览。" : "。请先切换到适配角色 " + pack.SkinId + "。");
        }
        catch (Exception ex) { _status.Text = "导入失败：" + ex.Message; Program.Log(ex); }
        finally { _working = false; RefreshButtons(); }
    }

    private async Task SelectAsync()
    {
        if (_working || _pet.IsSceneLoading || _packs.SelectedItem is not SceneAnimationPack pack || !MatchesSkin(pack)) return;
        _working = true; RefreshButtons();
        try { _pet.Show(); await _pet.SelectScenePackAsync(pack); _status.Text = _pet.SceneStatus; }
        catch (Exception ex) { _status.Text = "加载失败：" + ex.Message; Program.Log(ex); }
        finally { _working = false; RefreshButtons(); }
    }

    private async Task PreviewAsync()
    {
        if (_working || _pet.IsSceneLoading || _packs.SelectedItem is not SceneAnimationPack pack || !MatchesSkin(pack) || _actions.SelectedItem is not ActionChoice action) return;
        _working = true; RefreshButtons();
        try
        {
            _pet.Show();
            if (_pet.CurrentScenePack?.Id != pack.Id && !await _pet.SelectScenePackAsync(pack)) { _status.Text = _pet.SceneStatus; return; }
            await _pet.PreviewSceneAsync(action.Id); _status.Text = _pet.SceneStatus;
        }
        catch (Exception ex) { _status.Text = "预览失败：" + ex.Message; Program.Log(ex); }
        finally { _working = false; RefreshButtons(); }
    }

    private sealed record ActionChoice(string Id, string Label);
    private static string ActionLabel(string id) => id switch
    {
        "sing" => "唱 Let Me Go（含音乐）", "working" => "工作中", "attention" => "需要关注", "complete" => "任务完成",
        "walk" => "行走", "walk-left" => "向左行走", "walk-right" => "向右行走", "drag" => "拎起与挣扎",
        "fall" => "下落", "land" => "落地缓冲", "jump" => "跳跃", "idle" => "待机",
        "jump-left" => "向左跳跃", "jump-right" => "向右跳跃", "climb-left" => "向左侧攀爬", "climb-right" => "向右侧攀爬",
        "climb-top-left" => "向左翻上窗口", "climb-top-right" => "向右翻上窗口", _ => PetPerformanceCatalog.Label(id)
    };
}
