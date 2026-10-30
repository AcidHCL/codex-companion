using System;
using System.Drawing;
using System.Windows.Forms;

namespace DesktopPet;

internal sealed class TrayService : IDisposable
{
    private readonly NotifyIcon icon;
    private readonly ContextMenuStrip menu;
    private bool disposed;
    public event Action? ShowRequested;
    public event Action? HideRequested;
    public event Action? SettingsRequested;
    public event Action? SkinsRequested;
    public event Action? ExitRequested;
    public event Action? DashboardRequested;
    public event Action? ChatRequested;
    public event Action<bool>? MenuOpenChanged;

    public TrayService()
    {
        menu = new ContextMenuStrip();
        menu.Opened += (_, _) => MenuOpenChanged?.Invoke(true);
        menu.Closed += (_, _) => MenuOpenChanged?.Invoke(false);
        menu.Items.Add(new ToolStripLabel(AppVersion.ProductLabel) { Name = "AppVersionLabel", ForeColor = Color.DimGray });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("和桌宠聊天…", "Chat with your pet…"), null, (_, _) => ChatRequested?.Invoke());
        menu.Items.Add(L.T("Codex 工作看板…", "Codex work dashboard…"), null, (_, _) => DashboardRequested?.Invoke());
        menu.Items.Add(L.T("显示桌宠", "Show pet"), null, (_, _) => ShowRequested?.Invoke());
        menu.Items.Add(L.T("隐藏桌宠", "Hide pet"), null, (_, _) => HideRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("设置…", "Settings…"), null, (_, _) => SettingsRequested?.Invoke());
        menu.Items.Add(L.T("角色换装…", "Character wardrobe…"), null, (_, _) => SkinsRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("退出桌宠", "Quit AI Pet"), null, (_, _) => ExitRequested?.Invoke());
        icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Text = AppVersion.ProductLabel + L.T(" · 桌宠", " · Desktop pet"),
            ContextMenuStrip = menu,
            Visible = true
        };
        icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text)
    {
        if (!disposed) icon.ShowBalloonTip(3500, title + " · " + AppVersion.Label, text, ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
    }
}
