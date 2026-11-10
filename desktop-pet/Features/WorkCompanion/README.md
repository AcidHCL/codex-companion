# Proactive companion / 主动陪伴

This optional module implements **layer 1: lightweight desktop companionship** for work/study,
gaming, media and everyday use. It is disabled by default. Enable it in **Settings → Proactive companion**.

本模块实现第一层「主动陪伴」，覆盖工作、游戏、影音和日常，默认关闭，在「设置 → 主动陪伴」开启。
第一层自身只读前台应用名、活动时长；窗口标题默认不读取，需要独立开启。本层没有截图、OCR、
正文读取、按键内容记录、网页访问或长期记忆。输入活动只使用 Windows 最后输入时间。
可另行开启独立的 [ScreenUnderstanding 第二层](../ScreenUnderstanding/README.md)，按需理解当前窗口并记住应用分类。
目录名、类型名和设置文件名保留 WorkCompanion，以兼容现有配置与可选构建开关。

## Activity contexts (1.36.0)

- `CompanionActivityClassifier` uses exact process basenames and bounded user overrides. It does not inspect
  titles, executable paths, child processes or game memory. Browsers, launchers and unknown apps default to daily use.
- Four independent switches and reminder intervals: work 45, game 60, media 60, daily 45 minutes by default.
  Changing activity resets duration; changing apps within an activity preserves it. Five minutes without input
  resets the estimate. This is active use, not playback or match duration; idle/controller-only use may not be counted.
- Candidates carry current and previous activity hints. The bilingual prompt can adapt its tone but must not infer
  game victories, scores, media content, conversations, feelings or actual task progress.
- Full-screen quiet is on by default. App/category and input timing remain transiently available, allowing a later
  change from a game to another app to retain context. Full-screen titles are never read. No model request or
  companion reply is made while this quiet option is enabled, and suppressed events are not replayed later.
- Per-app overrides are explicit user preferences in the feature options, not automatically learned observations.
  Excluded/secure apps still take precedence. Disabling all four contexts stops observation entirely.

## Isolation boundary

- `Core`: observations, trigger policy, controller, host interface and optional `IWorkContextExtension` seam. Synthetic tests do not read the desktop.
- `Sensors`: read-only Windows foreground/session/idle observer. Excluded applications are checked before titles are read.
- `Adapters`: stateless selected-provider planner and small partial-class adapters for existing chat/pet presentation.
- `Settings`: separate options file and bilingual view. No observation/title/response is persisted.
- `WorkCompanionModule`: subscriptions, two-second timer, lifecycle and composition.

The application registers one optional module in `OptionalPetFeatures`, passes its generic settings contribution
to `AppSettingsWindow`, and disposes it on exit. `DesktopSettings` and the chat/memory database are unchanged.
`PetSpeechService.HasPendingSpeech` is a generic busy indicator covering synthesis as well as playback.

Build without this feature with `dotnet build -p:EnableWorkCompanion=false` (or the same property with `publish`).
This excludes the entire feature directory, its partial adapters and settings tab. No module configuration is loaded.
The dependent ScreenUnderstanding layer is excluded too. Use `EnableScreenUnderstanding=false` instead to retain layer one alone.
To remove it permanently, remove its guarded registration and its directory; generic settings navigation remains usable.

## Behavior and limits

- Sample every 2 seconds only when enabled, observing, and not manually paused.
- Require a stable foreground window for 15 seconds and an input pause of at least 5 seconds.
- In layer one alone, the first observation establishes a baseline without a greeting. An enabled content extension may
  request analysis after that window becomes stable. Five minutes away resets the work period.
- Default reaction cooldown: 5 minutes; failed/silent attempts are also limited across all activities.
- Locked/disconnected sessions, password managers and user exclusions suppress observation. Fullscreen is handled as above.
- Any window/context change, renewed input, pet interaction, typing in chat, or model change discards stale replies.
- Layer-one candidates expire after 30 seconds; generation has a 25-second deadline. The optional screen extension
  uses a 50-second candidate and 45-second overall deadline to allow bounded capture. No backlog is replayed.
- Uses the globally selected service and model. In Ollama mode only the selected **already-resident** model is considered. No automatic preload or explicit unload is sent.
  Existing context allocation and configured retention policy are preserved. A single native GPU sample may defer
  a response under load; it is an estimate, not proof of another caller or task.
- API mode uses the current configured model on demand, without Ollama residency or GPU checks. Existing context,
  cooldown, pause and observation permissions continue to apply. Requests use the API provider's billing;
  an unrelated local Ollama workload cannot block the selected API.
- Ollama does not provide an atomic “infer only if still resident” operation. An outside caller unloading a model
  between the residency check and inference is a server-side race that the client cannot completely eliminate.
- Reuse the existing chat cancellation/serialization lane, but send only a brief character instruction and the
  bounded current observation. Chat messages, drafts, shared memories and web tools are absent from the request.
- Replies are short and optional. Only happy/surprised expressions are allowed. No movement, songs or tool execution.
- Speech is separately opt-in and still respects the existing master speech switch. Active speech/bubbles take priority.
- Codex transitions are used to defer background remarks. Existing task notices, summaries, speech and celebrations
  remain authoritative; this module does not generate a duplicate completion announcement. Coordination uses the work switch.

Settings alone are stored at `%LOCALAPPDATA%/AIPet/work-companion-settings.json`. Pause and observations are transient.
The last reaction shown in the settings page is in memory only; “clear” removes it and resets the current work period.
Optional titles may contain private text: if enabled they are sent to the currently selected Ollama or API endpoint with the observation.
An endpoint on another computer has the same destination semantics as normal chat.

## Second layer and future work

The opt-in [ScreenUnderstanding](../ScreenUnderstanding/README.md) module now supplies layer two through
`IWorkContextExtension`: it can recall an app category, request bounded content analysis and propose a short reply.
It runs inside the same serialized/cancellable chat lane. After generation, this controller samples the foreground again
and validates input, expiry and host context before calling `FinishCandidate`; only then can the extension accept its
classification. A `ContextOnly` reply permits a valid silent update without a bubble or speech. Cancelled/stale candidates
are rejected. Raw content never enters this layer's observation records.

Layer two owns a small app-category store with expiry and review, separate from chat/shared memory. A future third layer
could add broader work goals, task continuity and preference memory; those capabilities are not implemented here.
Do not silently add screenshot/body fields to `WorkObservation` or write automatic observations to `ChatMemoryStore`.
Keep shared scheduling and UI priority in the host boundary, and require explicit scope/retention controls for new sources.

## Checks

Run isolated checks with `--work-companion-test`, `--work-companion-settings-test`,
`--work-companion-ui-test` and `--work-companion-navigation-test` in separate processes.
They use fake observations, in-memory HTTP and chat storage, and injected settings paths. They never load real models.
Also build with `EnableWorkCompanion=false` to verify that the optional boundary remains removable.

## 1.37.1 触发与诊断

临时界面忙碌、旧请求排空和全局冷却不会消耗尚未开始的机会；下次采样重新验证窗口和输入后生成新候选。已经开始的请求仍限频，不补播过期结果。周期健康检查跳过未完成的共享反应请求，显式用户刷新保持优先。

真实模块启动后，`CompanionDiagnostics` 只记录固定时间/层级/原因代码；测试默认不启用。日志位于 `%LOCALAPPDATA%/AIPet/companion-diagnostics.jsonl`，最多128 KiB，加一份同样大小的轮转文件，同类原因60秒去重。设置页“最近陪伴记录”按需显示，不保存标题、应用名、屏幕内容或模型输出。

`--companion-runtime-test` 在独立桌面环境中只读检查真实选中模型的驻留/视觉能力、开关、当前窗口可用性和最近原因；不读取正文、截图或发送推理，也不改变设置。其报告不代表历史未记录的原因。
