# AI Pet 1.21.0

Windows 桌面伙伴：可换皮肤、播放场景动画、与桌面窗口互动，并可选连接 Codex 看板、Ollama 聊天、本地语音输入与参考音色播报。

这是**源码与资源包**，不包含已编译的 EXE、DLL、Python 虚拟环境、模型权重、个人设置、配对材料或聊天记录。源码中的预编译浏览器 JavaScript 是界面运行资源；桌面程序需要自行构建。

## 环境要求

- Windows 11 x64。
- .NET 10 SDK，命令 `dotnet --version` 应显示 10 或更高版本；项目目标为 `net10.0-windows10.0.22000.0`。
- Microsoft Edge WebView2 Runtime，用于动画、视频和 Live2D。
- 可选：Ollama 与自行安装的聊天/图片理解模型。
- 可选：Python 3.12；参考音色播报另需兼容 NVIDIA GPU 与驱动。语音识别使用 CPU。

第一次还原 .NET / Python / Web 前端依赖需要联网。正常桌宠渲染和已安装语音模型可以离线运行；Codex、远程 Ollama 和联网搜索各自需要对应连接。

## 目录

```text
.
├─ README.md
├─ .gitignore
├─ 便携语音说明.txt
├─ Codex首次连接.txt
├─ Connect-Codex.ps1
└─ desktop-pet/
   ├─ DesktopPet.csproj
   ├─ *.cs
   ├─ build.ps1
   ├─ launch.ps1
   ├─ assets/
   ├─ live2d-web/
   ├─ scene-web/
   ├─ voice-service/
   └─ asr-service/
```

安装后的环境与模型使用根目录 `.tools/`，不会提交到仓库。配置模板采用相对路径；没有固定开发者机器地址。

## 构建与运行

在源码根目录打开 PowerShell：

```powershell
dotnet --version
.\desktop-pet\build.ps1 -Release
.\desktop-pet\launch.ps1
```

也可以直接执行：

```powershell
dotnet restore .\desktop-pet\DesktopPet.csproj
dotnet build .\desktop-pet\DesktopPet.csproj -c Release --output .\desktop-pet\dist
.\desktop-pet\dist\DesktopPet.exe
```

保留输出目录的资源与运行库，不能只移动生成的 EXE。普通构建依赖本机 .NET 10 Desktop Runtime。

需要自行发布为包含 .NET 运行时的程序目录时：

```powershell
dotnet publish .\desktop-pet\DesktopPet.csproj -c Release -r win-x64 --self-contained true --output .\artifacts\app
```

这条命令是给使用者自行编译的步骤；本源码包没有附带其输出。WebView2 Runtime、Python 和模型仍需单独准备。发布者还应保留全部第三方许可和素材来源。

普通 C# 构建直接使用 `assets/` 中的浏览器运行资源，不要求 Node。修改 Live2D Web 源码时可在 `desktop-pet/live2d-web` 使用其锁定依赖和构建说明重新生成运行资源；保留原版 Cubism Framework/Core 许可，专有 Core 不应重新压缩或混入自有代码。

## 使用

右键人物展开对话、设置、看板和更多图标；双击人物打开聊天。皮肤、大小、置顶、桌面行走、窗口互动与动画选择都可在设置中调整。退出使用托盘菜单。

DeepSeek 提供待机、工作、移动、拖拽、下落、落地、情绪及表演状态。素材是否可用取决于包内实际资源；内置新旧动画可切换。图片、Live2D 和场景动画的说明在 `desktop-pet/` 中。

### Ollama

先启动 Ollama 并安装适合本机硬件的模型，再在聊天连接设置中测试并保存服务根地址。默认 `http://127.0.0.1:11434`，自动读取已安装模型列表；支持识图的模型可接收拖入或选取的图片。

聊天、历史与共享记忆保存在当前 Windows 用户的数据目录。配置其它电脑的服务时，消息与图片发送到该服务。联网开关决定是否查询公开网页。

聊天模型可手动预加载和卸载，不删除权重，不修改 Ollama 全局设置。桌宠语音不会自动卸载聊天模型。

### 可选本地语音

参见 [便携语音说明](便携语音说明.txt)。在源码根目录，使用 Python 3.12：

```powershell
$python = (Get-Command python -ErrorAction Stop).Source
.\desktop-pet\voice-service\setup-fast.ps1 -PythonExe $python -WorkspaceRoot (Get-Location).Path -DownloadModel
.\desktop-pet\asr-service\setup.ps1 -PythonExe $python -WorkspaceRoot (Get-Location).Path -DownloadModel
```

两项可以单独安装。TTS 默认 `faster` 后端，读取 Qwen3-TTS-12Hz-1.7B-Base 与 `voice-service/reference.wav`；加载检查至少 7 GiB 空闲显存。ASR 使用多语言 Whisper small / CPU int8。没有参考人声文件时，需自行提供有权使用的音频。

看板可开关播报、预加载或卸载语音。启用后聊天期间保持模型，离开后恢复空闲释放；手动卸载优先。模型自然停顿与冷启动仍会影响响应。麦克风转写先进入输入框，经确认才发送。

### 可选 Codex 看板

按 [首次连接说明](Codex首次连接.txt) 在自己的 Codex 本地任务中配对。必须是同机同用户的官方 Codex App；不复制他人的连接记录。

默认读取额度与任务列表，消息正文单独开启。此功能依赖 App 的内部接口，更新后可能需要兼容性修复。任何身份或签名验证失败都不应绕过。

## 数据与发布

个人数据运行后保存在当前用户的 `%LOCALAPPDATA%\AIPet`，不属于源码包。聊天正文、图片和配对材料采用 Windows 当前用户加密，换电脑需重新配对。

不要提交 `.tools/`、`bin/`、`obj/`、`dist/`、`artifacts/`、缓存、日志、数据库、已安装路径配置、下载元数据或连接记录。不要用运行中的用户目录直接制作公开 ZIP。

## 许可与素材

当前项目本身**尚未声明统一开源许可证，授权范围待维护者明确**；本 README 不授予 MIT 或其它未经声明的许可。

保留所有第三方 LICENSE、NOTICE、THIRD-PARTY 文件及作者信息。Live2D、.NET、WebView2、Qwen3-TTS、faster-qwen3-tts、Whisper/CTranslate2 等遵守各自许可；详见资源与服务目录中的声明。

人物、歌曲、参考人声和示例模型的权利独立于程序代码。Let Me Go 参考来源的标注不是录音分发授权；发布者应自行确认是否可随包提供这些素材，使用者也可替换为有权使用的素材。代码或模型许可不会自动覆盖音乐与人物作品。
