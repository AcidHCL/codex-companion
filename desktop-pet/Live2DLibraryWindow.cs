using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace DesktopPet;

internal static class Live2DLibrary
{
    public static IReadOnlyList<Live2DModelInfo> Load(out IReadOnlyList<string> issues)
    {
        var catalog = new Live2DModelCatalog();
        var models = new List<Live2DModelInfo>(catalog.Load(out var importedIssues));
        var errors = new List<string>(importedIssues);
        string samples = Path.Combine(AppContext.BaseDirectory, "assets", "live2d-samples");
        if (Directory.Exists(samples))
            foreach (string file in Directory.EnumerateFiles(samples, "*.model3.json", SearchOption.AllDirectories))
                try
                {
                    string name = Path.GetFileName(file).Replace(".model3.json", "", StringComparison.OrdinalIgnoreCase);
                    models.Add(catalog.Validate(file, "sample-" + name.ToLowerInvariant(), name + L.T(" · 官方示例", " · Official sample")));
                }
                catch (Exception ex) { errors.Add(Path.GetFileName(file) + L.T("：", ": ") + ex.Message); }
        issues = errors.AsReadOnly();
        return models.AsReadOnly();
    }
}

internal sealed class Live2DLibraryWindow : Window
{
    private readonly PetWindow _pet;
    private readonly ListBox _models;
    private readonly TextBlock _status;
    private readonly Button _import;
    private readonly Button _select;
    private bool _working;

    public Live2DLibraryWindow(PetWindow pet)
    {
        _pet = pet;
        Title = AppVersion.WindowTitle(L.T("Live2D 皮肤", "Live2D skins"));
        Width = 620; Height = 590; MinWidth = 520; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Brush("#F4F6F0");
        var root = new Grid { Margin = new Thickness(26) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        var version = Ui.VersionLabel(); version.Margin = new Thickness(0, 0, 0, 7); header.Children.Add(version);
        header.Children.Add(Ui.Text(L.T("Live2D 皮肤", "Live2D skins"), 26, "#203E2C", true));
        var description = Ui.Text(L.T("导入已制作好的模型，在桌面上显示。\n选择解压后模型文件夹中的 .model3.json，相关素材会一起保存。", "Import a prepared model to display on your desktop.\nSelect its .model3.json file from the extracted folder; related assets are saved together."), 13, "#66815C");
        description.TextWrapping = TextWrapping.Wrap;
        description.LineHeight = 22;
        description.Margin = new Thickness(0, 12, 0, 18);
        header.Children.Add(description);
        root.Children.Add(header);
        _models = new ListBox { DisplayMemberPath = "Name", BorderBrush = Ui.Brush("#D3DFCF"), BorderThickness = new Thickness(1), Padding = new Thickness(8), FontSize = 15 };
        _models.SelectionChanged += (_, _) => RefreshButtons();
        _models.MouseDoubleClick += async (_, _) => await SelectAsync();
        Grid.SetRow(_models, 1); root.Children.Add(_models);
        var footer = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        _status = Ui.Text("", 12, "#597D61");
        _status.TextWrapping = TextWrapping.Wrap; _status.MaxHeight = 100;
        footer.Children.Add(_status);
        var note = Ui.Text(L.T("支持标准眨眼与呼吸；其他动作与表情后续加入。\n官方示例仅用于体验 Live2D；现有九款角色仍可在外观设置中选择。", "Standard blinking and breathing are supported; other motions and expressions will follow.\nThe official sample demonstrates Live2D. The nine character skins remain available in the wardrobe."), 11, "#7B8D78");
        note.TextWrapping = TextWrapping.Wrap; note.LineHeight = 18; note.Margin = new Thickness(0, 10, 0, 12);
        footer.Children.Add(note);
        var actions = new WrapPanel();
        _import = Ui.Button(L.T("导入模型…", "Import model…"), async (_, _) => await ImportAsync());
        _select = Ui.Button(L.T("使用选中模型", "Use selected model"), async (_, _) => await SelectAsync());
        _select.Margin = new Thickness(10, 0, 0, 0);
        var png = Ui.Button(L.T("恢复 PNG 皮肤", "Restore PNG skin"), (_, _) => { _pet.SelectSkin(_pet.CurrentSkin.Id); _pet.Show(); _status.Text = L.T("已恢复 ", "Restored ") + _pet.CurrentSkin.Name; });
        png.Margin = new Thickness(10, 0, 0, 0);
        actions.Children.Add(_import); actions.Children.Add(_select); actions.Children.Add(png);
        footer.Children.Add(actions);
        Grid.SetRow(footer, 2); root.Children.Add(footer);
        Content = root;
        _pet.SettingsChanged += OnPetChanged;
        Closed += (_, _) => _pet.SettingsChanged -= OnPetChanged;
        RefreshList();
    }

    private void RefreshList(string? selectedId = null)
    {
        var models = Live2DLibrary.Load(out var issues);
        _models.ItemsSource = models;
        _models.SelectedItem = models.FirstOrDefault(m => m.Id == (selectedId ?? _pet.CurrentLive2DModel?.Id)) ?? models.FirstOrDefault();
        _status.Text = issues.Count > 0 ? issues[0] : models.Count == 0 ? L.T("还没有模型。导入后即可选择使用。", "No models yet. Import one to select and use it.") : _pet.Live2DStatus;
        RefreshButtons();
    }

    private void RefreshButtons()
    {
        _import.IsEnabled = !_working;
        _select.IsEnabled = !_working && !_pet.IsLive2DLoading && _models.SelectedItem is Live2DModelInfo;
    }

    private void OnPetChanged()
    {
        _status.Text = _pet.Live2DStatus;
        RefreshButtons();
    }

    private async Task ImportAsync()
    {
        if (_working) return;
        var picker = new Microsoft.Win32.OpenFileDialog { Title = AppVersion.WindowTitle(L.T("选择 Live2D 模型", "Select a Live2D model")), Filter = L.T("Live2D 模型 (*.model3.json)|*.model3.json", "Live2D model (*.model3.json)|*.model3.json"), CheckFileExists = true, Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        _working = true; RefreshButtons();
        _status.Text = L.T("正在检查并导入模型…", "Checking and importing model…");
        try
        {
            var model = await new Live2DModelCatalog().ImportAsync(picker.FileName);
            RefreshList(model.Id);
            _status.Text = L.T("已导入 ", "Imported ") + model.Name + L.T("，点击“使用选中模型”即可显示。", ". Click Use selected model to display it.");
        }
        catch (Exception ex) { _status.Text = L.T("导入失败：", "Import failed: ") + ex.Message; Program.Log(ex); }
        finally { _working = false; RefreshButtons(); }
    }

    private async Task SelectAsync()
    {
        if (_working || _pet.IsLive2DLoading || _models.SelectedItem is not Live2DModelInfo model) return;
        _working = true; RefreshButtons();
        try { _pet.Show(); await _pet.SelectLive2DAsync(model); _status.Text = _pet.Live2DStatus; }
        finally { _working = false; RefreshButtons(); }
    }
}
