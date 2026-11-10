# Screen understanding / 屏幕理解

本模块实现第二层「按需理解当前窗口」，覆盖工作、游戏、影音和日常使用。
**默认关闭**，设置页位于「设置 → 屏幕理解」，并依赖已开启、未暂停的「主动陪伴」。
它可以理解可见内容并选择一句简短回应，也可以只更新本次理解结果，保持安静。
这不是持续录屏、自动操作电脑或通用长期工作记忆。

## 使用

1. 在「模型与语音」选择要使用的 Ollama 或 API 聊天模型。Ollama 需先加载；API 使用已保存的连接和当前模型，无需本地加载。
2. 在「主动陪伴」开启应用观察和需要的场景，再到「屏幕理解」开启此模块。
3. 选择允许读取窗口文字、必要时使用截图，以及是否记住应用分类。总开关默认关闭；这三个子选项初始为开启。
4. 自动理解遵守最少间隔，默认 5 分钟，可选 3 / 5 / 10 分钟；主动陪伴自身的回应间隔也继续生效。
5. 想主动试一次时，点击「切回目标窗口后看一下」，随后切回目标应用，保持窗口稳定约 15 秒，并停止输入至少 5 秒。
   这会预约一次理解，不会把设置窗口当作目标。手动请求可以绕过普通自动回应间隔，仍服从模型忙碌、排除应用、全屏安静等条件。

锁屏、会话断开、密码管理器和排除应用会被跳过。全屏安静沿用第一层设置，默认开启。
切换窗口、恢复输入、操作桌宠、开始聊天或更改模型/语言/角色，会取消或丢弃过期分析。
模型未就绪时不读取窗口文字或截图。正在聊天、播放语音或显示其他提示时，原有互动优先。

## 读取与模型

- 只针对匹配的当前前台窗口。文字由 Windows UI Automation 尝试获取，最多 4000 字符；部分应用不能提供可用文字。
- 截图是可选输入。开启后，手动请求、未知应用、游戏/影音等需要视觉信息的场景可使用截图；普通场景优先文字，文字很少时再尝试截图。
  关闭文字读取而只开启截图时，需要可用的视觉模型。这里没有独立的 OCR 服务。
- 截图最长边 1280 像素，JPEG 不超过 1 MiB。文字和图片还会按当前模型的上下文容量缩减输入预算。
- 读取运行在短命辅助进程中，单次最多等待 8 秒，避免第三方应用的 UIA/窗口绘制阻塞桌宠界面。
  无法安全读取、内容为空或窗口身份变化时保持安静。
- **全程使用当前选中的 Ollama 或 API 模型**；不会自动选择其他模型，也不会从 API 回退到 Ollama。
  Ollama 只使用已经驻留的模型：文字需要至少 4K 驻留上下文，图片还需要模型明确支持视觉且驻留上下文至少 8K。
  API 沿用连接中声明的图片能力，按保守的 8K 输入预算处理；这只是请求限制，不代表远程模型实际上下文上限。
  不满足图片条件时可使用已开启的文字通道；不会为了识图另选模型。
- Ollama 请求保留驻留模型当前的 `num_ctx` 和桌宠已配置的 `keep_alive`，不修改 Ollama 全局设置，也不预加载或卸载。
  生成前会再次检查驻留状态。Ollama 没有原子性的“仅在仍驻留时推理”接口，外部程序在检查与提交之间卸载模型的竞态仍无法完全消除。
- API 在捕获前只检查配置和输入能力，不发送“测试推理”或查询 Ollama 驻留状态。实际调用按 API 服务商规则计费，失败时保持安静，不混用其他模型。
- 窗口内容发往当前选中的模型服务。默认仅允许本机回环地址；云端 API 或其他远程地址需要在本页开启「允许发送到远程模型服务（含 API）」。原有开关值不会因升级或切换连接自动改变。

本层处理限时 45 秒，候选有效期 50 秒，模型规划部分限时 25 秒；不会积压并补播旧结果。
短回应可使用 `happy` / `surprised` 表情，朗读沿用主动陪伴的朗读选项和语音总开关。
没有移动、唱歌、浏览网页或执行任意动作的接口。

## 独立的应用分类记忆

模型只能输出固定结构：场景分类、可信度、是否适合复用、临时摘要、短回应和允许的表情。
窗口正文、图片、标题和应用名都作为不可信资料；其中的指令、链接、要求记忆的内容不提供工具权限。
模型只能根据可见证据判断，不能凭游戏画面猜胜负、声称听到声音或知道隐藏内容。

只有满足以下条件的分类可以保存：

- 应用尚无内置分类和用户手动指定分类；不是已知的浏览器、终端、编辑器、远程桌面、启动器等多用途宿主。
- 模型返回可复用的明确应用用途，可信度至少 **0.85**。
- 推理完成后，控制器再次确认目标窗口、输入空闲、会话、取消状态及桌宠上下文仍然有效。

持久化字段只有应用名、由规范化可执行路径及应用名计算的 SHA-256 身份哈希、分类、可信度、时间和用户纠正标记；
不保存原始路径。相同进程名的不同可执行文件按身份区分。最多 256 条，**90 天过期**。
这里保存的是工作 / 游戏 / 影音 / 日常分类，不是屏幕事实、待办或提示词；分类仍是推断，可能需要纠正。
浏览器等多用途应用可以理解当前页面，但不会凭单个页面建立固定分类。

在「应用识别记忆」中可以选择记录、纠正分类、删除记录或全部清空。
用户纠正后的记录不会被自动识别覆盖；「主动陪伴」里的手动应用分类优先于这里的记录。
删除后未来可能重新识别；关闭「记住可信的应用识别结果」会停止学习和使用此分类库，不会自动删除已有记录。

存储位置：

| 文件 | 内容 |
| --- | --- |
| `%LOCALAPPDATA%/AIPet/screen-understanding-settings.json` | 本模块选项 |
| `%LOCALAPPDATA%/AIPet/learned-applications.json` | 有上限和有效期的应用分类 |

原始文字和截图不落盘，不进入聊天历史、共享记忆或日志。最近理解摘要只保留在本次运行的设置状态里；
关闭、重配或清除本段观察会清除临时状态。模型生成的短回应也不创建聊天回合。

## 代码边界与移除

- `Core`：窗口读取请求与结果契约。
- `Sensors`：前台身份校验、辅助进程、UIA 文字和窗口截图；读取前后都校验目标及会话。
- `Adapters`：按当前连接分派模型请求，保留 Ollama 驻留检查，提供 API 能力预检、独立多模态请求、严格 JSON 解析及模拟 HTTP 检查。
- `Memory`：只允许结构化分类的独立存储，具有容量、过期和人工纠正规则。
- `Settings`：独立选项、双语页面及合成设置/UI 检查。
- `ScreenUnderstandingModule`：能力预检 → 按需读取 → 模型理解 → 等待控制器最终确认 → 接受分类结果。

`OptionalPetFeatures` 注册模块和设置页。第一层只通过 `IWorkContextExtension` 提供分类回忆、理解请求、
生成和最终确认等通用接口。原始正文与截图不进入第一层观察记录。
现有聊天/行为取消和串行调度通道保证用户互动优先；请求不访问聊天内容或 `ChatMemoryStore`。

```powershell
dotnet build -p:EnableScreenUnderstanding=false
dotnet publish -p:EnableScreenUnderstanding=false
```

以上开关会排除整个第二层目录、平台适配和设置页，保留第一层。
`-p:EnableWorkCompanion=false` 会同时排除两层。构建开关不删除已有用户设置或分类文件。
若永久移除，删除本目录及受 `SCREEN_UNDERSTANDING` 保护的注册/构建项即可；第一层的通用扩展接口可以保留。

## 诊断

以下入口在独立进程中运行，不加载真实模型，也不改写用户设置或分类库：

| 参数 | 检查内容 | 输出 |
| --- | --- | --- |
| `--screen-understanding-test` | 注入路径的设置/记忆、模拟 HTTP、模块调度与保存边界 | `screen-understanding-validation.json` |
| `--screen-understanding-ui-test` | 双语设置界面和合成记忆操作 | `screen-understanding-ui-validation.json` 与合成界面证据 |
| `--screen-capture-test` | 自建测试窗口的读取、保护规则和捕获结果 | `screen-capture-validation.json` |

捕获诊断仅操作自建测试窗口，不读取个人窗口。`--screen-capture-worker` 是内部辅助进程入口，不是手动诊断命令。
第一层的 `--work-companion-test` 还覆盖静默分类提交和窗口/输入变化后拒绝过期结果。
检查功能可移除性时，分别构建关闭第二层和关闭第一层的版本；这些命令不是本说明对检查已通过的声明。

## English quick guide

Enable **Settings → Proactive companion**, then opt into **Settings → Screen understanding**.
The module uses the currently selected Ollama or API model, with no cross-provider fallback. Ollama must already be loaded;
its resident context and retention are preserved without preloading or unloading. API uses the saved connection and declared
image capability with a conservative 8K input budget. API usage follows provider billing; preflight performs no inference.
Screenshots require image support. Cloud API and other remote transmission require the existing **Allow sending to a remote
model service (including API)** switch; switching providers never enables it automatically.

For a manual check, click **Inspect after I return to the target window**, switch back to that application and pause
input. A stable window and the existing exclusions, full-screen quiet and interaction priorities are still required.
Automatic analysis is bounded and infrequent; it is not screen recording. Text/images and generated summaries are
transient and never enter chat history or shared memory.

Only dedicated-app categories with confidence at least 0.85 may enter the feature's separate store after final
foreground validation. Entries contain an app name, identity hash, category and limited metadata, expire after 90 days,
and can be corrected or deleted. Multipurpose apps are not assigned a lasting category from one page. Disable
`EnableScreenUnderstanding` at build time to remove this layer while retaining the lightweight companion.
