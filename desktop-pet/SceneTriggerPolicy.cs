using System;
using System.Collections.Generic;
using System.Linq;

namespace DesktopPet;

// Polls include unrelated message/usage updates. Completion is verified separately by CodexMonitor.
internal sealed class SceneTriggerPolicy
{
    public string? PersistentAction { get; private set; }
    public bool HasRunningTasks { get; private set; }
    public void Observe(CodexSnapshot snapshot)
    {
        var tasks = snapshot.Tasks.Where(t => t.Source == TaskSource.Codex).ToArray();
        HasRunningTasks = snapshot.Connected && tasks.Any(t => t.IsRunning);
        PersistentAction = !snapshot.Connected ? null : tasks.Any(t => t.NeedsAttention) ? "attention" : HasRunningTasks ? "working" : null;
    }

    internal static IReadOnlyList<string> RunChecks()
    {
        var policy = new SceneTriggerPolicy();
        var checks = new List<string>();
        CodexTaskInfo Task(string id, string status, TaskSource source = TaskSource.Codex) => new(id, id, status, "codex", null, null, 0, false) { Source = source };
        CodexSnapshot Snapshot(params CodexTaskInfo[] tasks) => CodexSnapshot.Empty with { Connected = true, Tasks = tasks };
        void Check(bool ok, string name) { if (!ok) throw new InvalidOperationException(name); checks.Add(name); }
        policy.Observe(Snapshot(Task("a", "completed")));
        Check(policy.PersistentAction == null, "首次读取不播放历史完成动作");
        policy.Observe(Snapshot(Task("a", "active")));
        Check(policy.PersistentAction == "working", "运行任务触发工作");
        policy.Observe(Snapshot(Task("a", "active")));
        Check(policy.PersistentAction == "working", "重复轮询保持相同场景");
        policy.Observe(Snapshot(Task("a", "idle")));
        Check(policy.PersistentAction == null, "空闲不代表成功完成");
        policy.Observe(Snapshot(Task("a", "waitingForInput")));
        Check(policy.PersistentAction == "attention", "等待输入触发关注");
        policy.Observe(Snapshot(Task("a", "error"), Task("b", "active")));
        Check(policy.PersistentAction == "attention", "错误优先于工作场景");
        policy.Observe(Snapshot(Task("codex", "idle"), Task("dot", "active", TaskSource.Dot)));
        Check(policy.PersistentAction == null && !policy.HasRunningTasks, "Dot 执行任务不触发主桌宠工作或停止散步");
        policy.Observe(Snapshot(Task("codex", "active"), Task("dot", "error", TaskSource.Dot)));
        Check(policy.PersistentAction == "working" && policy.HasRunningTasks, "Dot 待处理任务不覆盖 Codex 工作场景");
        policy.Observe(Snapshot(Task("unknown", "active", TaskSource.Unknown), Task("dot", "waitingForInput", TaskSource.Dot)));
        Check(policy.PersistentAction == null && !policy.HasRunningTasks, "未知来源与 Dot 均不影响主桌宠状态");
        policy.Observe(Snapshot());
        Check(policy.PersistentAction == null, "任务移出列表恢复待机");
        policy.Observe(Snapshot(Task("a", "active")));
        policy.Observe(CodexSnapshot.Empty);
        Check(policy.PersistentAction == null, "断线恢复待机");
        return checks;
    }
}
