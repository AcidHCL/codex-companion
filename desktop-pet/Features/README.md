# Optional features / 可选功能模块

桌宠的主动陪伴按层组织，由 `OptionalPetFeatures` 注册生命周期和设置页。
功能代码、独立设置、诊断和平台适配保留在各自目录；聊天窗口和桌宠只提供通用的调度、取消与展示接口。

| 模块 | 范围 | 运行时默认 | 构建开关 |
| --- | --- | --- | --- |
| [WorkCompanion](WorkCompanion/README.md) | 第一层：前台应用、可选标题、活跃时长、场景开关和回应优先级 | 关闭 | `EnableWorkCompanion` |
| [ScreenUnderstanding](ScreenUnderstanding/README.md) | 第二层：按需读取当前窗口文字/截图，以及独立的应用分类记忆 | 关闭，需要第一层开启 | `EnableScreenUnderstanding` |

第一层的观察器本身不读取正文或截图。第二层通过 `IWorkContextExtension` 扩展它，
复用稳定窗口、输入暂停、排除应用、全屏安静、模型调度和过期结果检查。
原始窗口内容只在第二层流转，不加入 `WorkObservation`、聊天历史或共享记忆库。
第二层仅允许把经过校验的应用身份与分类写入自己的记忆文件，不是通用长期工作记忆。

两个目录默认参与构建，但是否观察由各自设置控制。构建时可以排除：

```powershell
dotnet build -p:EnableScreenUnderstanding=false
dotnet build -p:EnableWorkCompanion=false
```

第一条保留第一层，移除屏幕理解、其设置页和平台适配；第二条同时移除两层。
相同参数适用于 `dotnet publish`。排除的模块不会加载其设置或记忆文件；本机已有数据不会被自动删除。
诊断入口、模块约束和合成测试见各目录说明。

The optional modules are composed through `OptionalPetFeatures`. Layer one owns lightweight
foreground/activity observations and reaction scheduling; it does not capture content itself.
Layer two adds opt-in current-window text/images and a separate, bounded app-category store through
`IWorkContextExtension`. Raw content never becomes chat history or shared memory. Both features are
off at runtime by default; disabling the layer-one build flag excludes both layers.
