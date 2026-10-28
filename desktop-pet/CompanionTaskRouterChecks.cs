using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

internal static class CompanionTaskRouterChecks
{
    internal static IReadOnlyList<string> Run()
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        CodexTaskInfo Task(string id, string status, TaskSource source) =>
            new(id, id, status, "codex", null, null, 0, false) { Source = source };
        CodexSnapshot Snapshot(params CodexTaskInfo[] tasks) => CodexSnapshot.Empty with { Connected = true, Tasks = tasks };

        var router = new CompanionTaskRouter();
        int notifications = 0;
        router.Changed += () => notifications++;
        Check(router.MainPet.Activity == CompanionActivity.Disconnected && router.Dot.Activity == CompanionActivity.Disconnected,
            "同伴框架初始无任务且未连接");

        var mixed = Snapshot(Task("c", "active", TaskSource.Codex), Task("d", "waitingForInput", TaskSource.Dot), Task("u", "error", TaskSource.Unknown));
        router.Observe(mixed);
        Check(router.MainPet.Activity == CompanionActivity.Working && router.Dot.Activity == CompanionActivity.Attention,
            "Codex 工作与 Dot 待处理分别计算动画意图");
        Check(router.MainPet.Tasks.Select(t => t.Id).SequenceEqual(new[] { "c" }) && router.Dot.Tasks.Select(t => t.Id).SequenceEqual(new[] { "d" }),
            "未知来源不进入任何人物任务流");
        Check(router.MainPetSnapshot.Tasks.Select(t => t.Id).SequenceEqual(new[] { "c" }) && router.MainPetSnapshot.Connected,
            "主桌宠快照仅含 Codex 且保留连接状态");
        Check(!router.DotCompanionEnabled && !router.DotCompanionVisible,
            "Dot 状态可以更新但显示入口始终关闭");

        router.Observe(mixed with { Refreshing = true, StatusText = "正在同步…" });
        Check(notifications == 1, "重复轮询与同步提示不重启动画意图");
        Check(router.MainPetSnapshot.Refreshing && router.MainPetSnapshot.StatusText == "正在同步…",
            "用量和连接刷新仍传递给主桌宠快照");

        router.Observe(Snapshot(Task("c", "idle", TaskSource.Codex), Task("d", "active", TaskSource.Dot)));
        Check(router.MainPet.Activity == CompanionActivity.Idle && router.Dot.Activity == CompanionActivity.Working,
            "只有 Dot 工作时主桌宠保持待机");
        router.Observe(mixed with { Connected = false });
        Check(router.MainPet.Activity == CompanionActivity.Disconnected && router.Dot.Activity == CompanionActivity.Disconnected,
            "断线时两个同伴均停止展示运行意图");

        var callerTasks = new List<CodexTaskInfo> { Task("c", "active", TaskSource.Codex), Task("d", "active", TaskSource.Dot) };
        router.Observe(CodexSnapshot.Empty with { Connected = true, Tasks = callerTasks });
        var priorMain = router.MainPet;
        var priorDot = router.Dot;
        callerTasks.Clear();
        Check(priorMain.Tasks.Count == 1 && priorDot.Tasks.Count == 1 && router.MainPetSnapshot.Tasks.Count == 1,
            "外部任务列表改变不会修改已发布的同伴状态");
        bool readOnly = false;
        try { ((IList<CodexTaskInfo>)priorMain.Tasks).Clear(); }
        catch (NotSupportedException) { readOnly = true; }
        Check(readOnly, "已发布任务集合为只读");
        router.Observe(Snapshot());
        Check(router.MainPet.Activity == CompanionActivity.Idle && router.Dot.Activity == CompanionActivity.Idle &&
            priorMain.Activity == CompanionActivity.Working && priorDot.Tasks.Count == 1,
            "新轮询替换状态而不改变前一轮不可变快照");
        Check(!router.DotCompanionEnabled && !router.DotCompanionVisible, "任何任务状态都不会自动上线 Dot 悬浮图像");
        return checks;
    }
}
