# AI Pet / Codex Companion 工作约定

本文件适用于本项目；与其他仓库的个人工作流无关。

## UI 设计原则

修改界面前遵循 [DESIGN.md](DESIGN.md)。核心原则：

> Avoid unnecessary secondary labels and descriptions. Let the layout do the explaining.

优先通过分组、对齐、间距、控件顺序与状态表达关系；不在每个标题、字段或按钮下重复解释其用途。保留用户作决定所需的状态、错误和数据发送提示。所有界面变更同步考虑中英文、键盘操作与最小窗口尺寸。

## 修改边界

- UI 精简不得改变功能、保存语义或现有权限开关。
- 不把用户配置、密钥、聊天记录、记忆数据库或本机绝对路径加入公开源码。
- 仅在用户要求时提交或推送 GitHub。
