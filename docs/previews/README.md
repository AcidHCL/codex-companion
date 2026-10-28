# README 预览素材

这些图片直接由仓库内已有素材缩放、转码得到，没有重绘或修改应用的原始资源。

- `skins/`：九张角色立绘的 320×320 透明 PNG 缩略图。
- `classic/`：`desktop-pet/assets/scenes/deepseek-small` 的旧版动画 GIF，保留完整画布、原始帧顺序和每帧时长。
- 拖拽预览包含一次 `start` 和两次 `loop`，展示被拎起后挣扎的过程。
- GIF 统一使用浅色底和共享调色板，避免二值透明导致半透明边缘变色；唱歌预览不带音频。
- GIF 为方便展示而循环，不改变软件里的动作触发、循环或单次播放策略。所有原始资源及其许可仍保持原样。

在仓库根目录重建（需要 Python 和 Pillow）：

```sh
python -m pip install Pillow
python tools/build_readme_previews.py
```

`sources.json` 记录仓库相对来源、SHA-256、帧数及时间，不包含本机路径或账号信息。
