using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace DesktopPet;

// The application knows only optional lifecycle and settings contributions.
// Feature-specific adapters, observers and policy remain in their own directory.
internal sealed record PetSettingsPage(string Key, string Title, Func<FrameworkElement> CreateView);

internal sealed class OptionalPetFeatures : IDisposable
{
    private readonly List<IDisposable> _modules = new();
    internal IReadOnlyList<PetSettingsPage> SettingsPages { get; }

    internal static int? RunEarlyWorker(string[] args)
    {
        if (!args.Contains("--screen-capture-worker")) return null;
#if SCREEN_UNDERSTANDING
        return ScreenUnderstanding.ScreenCaptureWorker.Run();
#else
        return 2;
#endif
    }

    internal static int? RunDiagnostic(string[] args)
    {
        if (args.Contains("--companion-runtime-test"))
        {
#if WORK_COMPANION
            return WorkCompanion.CompanionRuntimeProbe.Run();
#else
            return 2;
#endif
        }
        if (args.Any(arg => arg is "--screen-understanding-test" or "--screen-understanding-ui-test" or "--screen-capture-test"))
        {
#if SCREEN_UNDERSTANDING
            return args.Contains("--screen-understanding-ui-test") ? ScreenUnderstanding.ScreenUnderstandingSettingsUiChecks.Run() :
                ScreenUnderstanding.ScreenUnderstandingDiagnostics.Run(args.Contains("--screen-capture-test"));
#else
            return 2;
#endif
        }
        string? flag = args.FirstOrDefault(arg => arg is "--work-companion-test" or
            "--work-companion-settings-test" or "--work-companion-ui-test" or "--work-companion-navigation-test" or "--work-companion-probe-test");
        if (flag == null) return null;
#if WORK_COMPANION
        return flag switch
        {
            "--work-companion-probe-test" => WorkCompanion.WorkCompanionDiagnostics.RunObserverProbe(),
            "--work-companion-settings-test" => WorkCompanion.WorkCompanionSettingsChecks.Run(),
            "--work-companion-ui-test" => WorkCompanion.WorkCompanionSettingsUiChecks.Run(),
            "--work-companion-navigation-test" => WorkCompanion.WorkCompanionNavigationChecks.Run(),
            _ => WorkCompanion.WorkCompanionDiagnostics.Run()
        };
#else
        return 2; // An unavailable diagnostic must never fall through to the real app.
#endif
    }

    internal OptionalPetFeatures(PetWindow pet, CodexMonitor? monitor,
        Func<OllamaChatWindow?> getChat, Func<OllamaChatWindow?> ensureChat)
    {
        var pages = new List<PetSettingsPage>();
#if WORK_COMPANION
        WorkCompanion.IWorkContextExtension? extension = null;
#if SCREEN_UNDERSTANDING
        var screen = new ScreenUnderstanding.ScreenUnderstandingModule();
        extension = screen;
#endif
        var work = new WorkCompanion.WorkCompanionModule(pet, monitor, getChat, ensureChat, extension);
        _modules.Add(work);
        pages.Add(new PetSettingsPage("WorkCompanion", L.T("主动陪伴", "Proactive companion"), work.CreateSettingsView));
#if SCREEN_UNDERSTANDING
        _modules.Add(screen);
        pages.Add(new PetSettingsPage("ScreenUnderstanding", L.T("屏幕理解", "Screen understanding"), screen.CreateSettingsView));
#endif
#endif
        SettingsPages = pages;
    }

    public void Dispose()
    {
        foreach (var module in _modules) module.Dispose();
        _modules.Clear();
    }
}
