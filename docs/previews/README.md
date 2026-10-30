# README 预览素材

这些图片直接由仓库内已有素材缩放、转码得到，没有重绘或修改应用的原始资源。

- `skins/`：九张角色立绘的 320×320 透明 PNG 缩略图。
- `classic/`：`desktop-pet/assets/scenes/deepseek-small` 的旧版动画 GIF，保留完整画布、原始帧顺序和每帧时长。
- `gpt/`：`desktop-pet/assets/scenes/gpt-small` 的 GPT 小龙娘动画 GIF，同样保留完整画布、原始帧顺序和每帧时长。
- 拖拽预览包含一次 `start` 和两次 `loop`，展示被拎起后挣扎的过程。
- GIF 统一使用浅色底和共享调色板，避免二值透明导致半透明边缘变色；唱歌预览不带音频。
- GIF 为方便展示而循环，不改变软件里的动作触发、循环或单次播放策略。所有原始资源及其许可仍保持原样。

在仓库根目录重建（需要 Python 和 Pillow）：

```sh
python -m pip install Pillow
python tools/build_readme_previews.py
```

`sources.json` 记录仓库相对来源、SHA-256、帧数及时间，不包含本机路径或账号信息。

## GPT 小龙娘预览

以下八段预览为 320×320 无声 GIF，使用与旧版 DeepSeek 预览相同的浅灰蓝底色。白发、龙角、龙翼与龙尾均来自现有透明 PNG；只做等比缩放、背景合成与共享调色板转码，不重绘、不补帧、不裁切。

| 文件 | 内容 | 编码帧数 | 一轮时长 |
| --- | --- | ---: | ---: |
| `gpt/idle.gif` | 待机 | 8 | 2.82 秒 |
| `gpt/working.gif` | 工作 | 8 | 0.88 秒 |
| `gpt/walk-right.gif` | 向右行走 | 8 | 0.84 秒 |
| `gpt/drag.gif` | 拎起后挣扎 | 20 | 2.24 秒 |
| `gpt/happy.gif` | 开心 | 6 | 1.66 秒 |
| `gpt/angry.gif` | 生气 | 6 | 1.62 秒 |
| `gpt/jump-right.gif` | 向右跳跃姿势 | 6 | 0.85 秒 |
| `gpt/climb-right.gif` | 向右攀爬姿势 | 6 | 0.82 秒 |

每帧时长直接读取 `gpt-small.petanim.json`，没有额外添加末尾停留。拖拽以 `start → loop → loop` 展示，其他动作展示一次 `loop`。所有 GIF 为 README 展示而无限循环；开心、生气、跳跃在软件中仍按原有单次动作播放，移动轨迹与窗口碰撞由软件控制，GIF 仅展示角色姿势。

仅更新 GPT 预览及其元数据，保留已有皮肤和 DeepSeek 文件：

```sh
python tools/build_readme_previews.py --only gpt
```

`sources.json` 的 `gpt` 部分额外记录动作清单的相对路径与 SHA-256、应用原始 `repeat` 值，以及 GIF 展示循环和零额外停留，便于核对原素材与预览的差异。
