using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DesktopPet;

// Independent synthetic chat windows; never load or save the user's configuration.
internal static class PetWebUiChecks
{
    internal static int Run(bool live)
    {
        string name = live ? "web-live" : "web-ui";
        string evidence = Path.Combine(AppContext.BaseDirectory, name + "-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        var checks = new List<string>();
        var answers = new List<string>();
        var app = new Application { Resources = Ui.CreateResources(), ShutdownMode = ShutdownMode.OnExplicitShutdown };
        OllamaChatWindow? window = null;
        int result = 1, saves = 0;
        void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); checks.Add(label); }
        app.Startup += async (_, _) =>
        {
            string? error = null;
            try
            {
                var settings = new DesktopSettings { CodexEnabled = false, WebSearchEnabled = true };
                var fakeModel = live ? null : new ModelFixture();
                var fakeWeb = live ? null : new WebFixture();
                IOllamaClient model = fakeModel is null ? new OllamaClient() : fakeModel;
                IPetWebClient web = fakeWeb is null ? new PetWebClient() : fakeWeb;
                window = new OllamaChatWindow(settings, () => "DeepSeek", _ => { }, answers.Add, () => saves++, model, webClient: web)
                    { ShowActivated = false, ShowInTaskbar = false, Topmost = false };
                window.Show();
                var models = Named<ComboBox>(window, "OllamaModelSelector");
                await Until(() => models.IsEnabled, 45);
                var input = Named<TextBox>(window, "OllamaChatInput");
                var enabled = Named<CheckBox>(window, "OllamaWebEnabled");
                Require(enabled.IsChecked == true && enabled.IsVisible, "web_toggle_enabled_and_visible");
                input.Text = live ? "请联网搜索 Ollama tool calling 官方文档，用一句中文说明用途，并给出来源。" : "请搜索今天的合成测试新闻，附来源。";
                await window.SendAsync();
                Require(Status(window).StartsWith("回复完成", StringComparison.Ordinal), "search_answer_completed");
                Require(Descendants<StackPanel>(window).Any(panel => panel.Name == "OllamaSources"), "search_sources_rendered_below_answer");
                var links = Descendants<TextBlock>(window).SelectMany(block => block.Inlines.OfType<Hyperlink>()).ToArray();
                Require(links.Length > 0 && links.All(link => link.NavigateUri.Scheme is "http" or "https"), "sources_are_clickable_web_links");
                Capture(window, Path.Combine(evidence, "search-answer.png"));
                if (live)
                {
                    models.SelectedValue = OllamaModels.QualityModel;
                    await Until(() => models.IsEnabled && settings.OllamaModel == OllamaModels.QualityModel, 45);
                    input.Text = "请读取这个网页，用一句中文概括页面主题并引用来源：https://ollama.com/blog/tool-support";
                    await window.SendAsync();
                    Require(Status(window).StartsWith("回复完成", StringComparison.Ordinal), "second_model_direct_webpage_completed");
                    Require(Descendants<StackPanel>(window).Count(panel => panel.Name == "OllamaSources") == 2, "each_live_answer_has_its_own_sources");
                    Capture(window, Path.Combine(evidence, "page-answer.png"));
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                    await model.UnloadAsync(OllamaModels.QualityModel, timeout.Token);
                }
                else
                {
                    Require(fakeWeb!.Searches == 1 && fakeModel!.ToolRounds == 1, "model_called_search_once");
                    Require(fakeModel!.FinalMessages.Any(message => message.Role == "user" && message.Content.Contains("外部不可信资料", StringComparison.Ordinal)), "untrusted_tool_results_reach_final_answer");
                    int networkCalls = fakeWeb.Searches + fakeWeb.Fetches;
                    enabled.IsChecked = false;
                    input.Text = "联网关闭时请不要访问 https://example.com/private";
                    await window.SendAsync();
                    Require(fakeWeb.Searches + fakeWeb.Fetches == networkCalls && fakeModel.ToolRounds == 1, "disabled_web_skips_all_page_and_search_requests");
                    Require(!settings.WebSearchEnabled && saves > 0, "web_toggle_preference_saved");
                    enabled.IsChecked = true;
                    input.Text = "读取这个页面 https://example.com/news";
                    await window.SendAsync();
                    Require(fakeWeb.Fetches > 0 && fakeModel.ToolRounds == 1, "explicit_link_read_without_new_tool_plan");
                    fakeWeb.Fail = true;
                    fakeModel.AnswerText = "去搜了一下，今天最高 14°C、最低 7°C。[1]";
                    int finalCalls = fakeModel.FinalCalls;
                    int sourcePanels = Descendants<StackPanel>(window).Count(panel => panel.Name == "OllamaSources");
                    input.Text = "读网页 https://example.com/blocked";
                    await window.SendAsync();
                    Require(Descendants<TextBlock>(window).Any(block => block.Name == "OllamaWebWarning" && block.Text.Contains("未能读取", StringComparison.Ordinal)), "network_failure_has_persistent_ui_warning_independent_of_model_answer");
                    Require(fakeModel.FinalCalls == finalCalls && answers.Last().Contains("无法核实", StringComparison.Ordinal) && !answers.Last().Contains("14°C", StringComparison.Ordinal) && !answers.Last().Contains("[1]", StringComparison.Ordinal), "failed_lookup_cannot_render_model_invented_weather_or_citation");
                    Require(Descendants<StackPanel>(window).Count(panel => panel.Name == "OllamaSources") == sourcePanels, "failed_lookup_does_not_borrow_prior_answer_sources");
                    Capture(window, Path.Combine(evidence, "failed-lookup.png"));
                    fakeWeb.Fail = false;
                    fakeModel.AnswerText = "这是合成测试回答，资料介绍了联网工具。[1]";
                    fakeWeb.FailedPaths.Add("/partial-blocked");
                    input.Text = "读取 https://example.com/partial-blocked 和 https://example.com/available";
                    await window.SendAsync();
                    Require(Descendants<TextBlock>(window).Any(block => block.Name == "OllamaWebWarning" && block.Text.Contains("已取得 1 个可用来源", StringComparison.Ordinal) && block.Text.Contains("部分", StringComparison.Ordinal)), "partial_lookup_displays_available_source_and_partial_warning");
                    Require(Descendants<StackPanel>(window).Count(panel => panel.Name == "OllamaSources") == sourcePanels + 1, "partial_lookup_renders_only_successful_current_source");
                    Require(fakeModel.FinalMessages.Any(message => message.Content.Contains("旧答复未经核实", StringComparison.Ordinal)) && !fakeModel.FinalMessages.Any(message => message.Content.Contains("14°C", StringComparison.Ordinal)), "failed_answer_history_is_marked_unverified_in_next_model_context");
                    Capture(window, Path.Combine(evidence, "partial-lookup.png"));
                    fakeWeb.Block = true;
                    input.Text = "读网页 https://example.com/slow";
                    Task pending = window.SendAsync();
                    await Until(() => fakeWeb.Started, 5);
                    Named<Button>(window, "OllamaStop").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await pending.WaitAsync(TimeSpan.FromSeconds(5));
                    Require(input.Text.Contains("/slow", StringComparison.Ordinal) && Status(window).StartsWith("已停止", StringComparison.Ordinal), "stop_cancels_network_and_restores_draft");
                    window.Width = 320; window.Height = 360; window.UpdateLayout();
                    var root = (FrameworkElement)window.Content;
                    var bounds = enabled.TransformToAncestor(root).TransformBounds(new Rect(enabled.RenderSize));
                    Require(bounds.Right <= root.ActualWidth && bounds.Bottom <= root.ActualHeight, "web_toggle_fits_compact_chat");
                    Capture(window, Path.Combine(evidence, "compact-web-toggle.png"));
                }
                result = 0;
            }
            catch (Exception ex) { error = ex.GetType().Name + ": " + ex.Message; }
            finally
            {
                window?.Shutdown();
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, name + "-validation.json"), JsonSerializer.Serialize(new
                { passed = result == 0, version = AppVersion.Number, assertions = checks.Count, checks, error, evidence, answers, live, userSettingsWrites = 0, syntheticInput = true }, new JsonSerializerOptions { WriteIndented = true }));
                app.Shutdown(result);
            }
        };
        app.Run(); return result;
    }

    private static string Status(Window window) => Named<TextBlock>(window, "OllamaChatStatus").Text;
    private static T Named<T>(DependencyObject root, string name) where T : FrameworkElement => Descendants<T>(root).Single(item => item.Name == name);
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static async Task Until(Func<bool> condition, int seconds)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private sealed class ModelFixture : IOllamaClient
    {
        internal int ToolRounds, FinalCalls;
        internal string AnswerText = "这是合成测试回答，资料介绍了联网工具。[1]";
        internal IReadOnlyList<OllamaMessage> FinalMessages = Array.Empty<OllamaMessage>();
        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(new[] { OllamaModels.FastModel });
        public Task<OllamaModelCapabilities> GetModelInfoAsync(string model, CancellationToken ct) => Task.FromResult(new OllamaModelCapabilities(true, true, false, true));
        public async IAsyncEnumerable<OllamaChatDelta> ChatAsync(string model, IReadOnlyList<OllamaMessage> messages, [EnumeratorCancellation] CancellationToken ct)
        {
            FinalCalls++; FinalMessages = messages; await Task.Delay(5, ct);
            yield return new(AnswerText, true, 20, 100);
        }
        public async IAsyncEnumerable<OllamaChatDelta> ChatWithToolsAsync(string model, IReadOnlyList<OllamaMessage> messages, IReadOnlyList<OllamaToolDefinition> tools, [EnumeratorCancellation] CancellationToken ct)
        {
            ToolRounds++; await Task.Delay(5, ct);
            yield return new("", true, ToolCalls: new[] { new OllamaToolCall("web_search", "{\"query\":\"合成测试新闻\"}") });
        }
        public Task UnloadAsync(string model, CancellationToken ct) => Task.CompletedTask;
        public void Dispose() { }
    }
    private sealed class WebFixture : IPetWebClient
    {
        internal int Searches, Fetches;
        internal bool Block, Started, Fail;
        internal HashSet<string> FailedPaths = new(StringComparer.Ordinal);
        public Task<PetWebResult> SearchAsync(string query, CancellationToken ct)
        { Searches++; return Task.FromResult(new PetWebResult(new[] { new PetWebSource("合成测试新闻", "https://example.com/news", "这是用于测试的公开消息摘要。") })); }
        public async Task<PetWebResult> FetchAsync(string url, CancellationToken ct)
        {
            Fetches++;
            if (Block) { Started = true; await Task.Delay(Timeout.Infinite, ct); }
            if (Fail || FailedPaths.Contains(new Uri(url).AbsolutePath)) return new PetWebResult(Array.Empty<PetWebSource>(), "合成网站限制自动读取。");
            return new PetWebResult(new[] { new PetWebSource("合成网页", url, "页面介绍模型调用工具读取公开信息。") });
        }
        public void Dispose() { }
    }
}
