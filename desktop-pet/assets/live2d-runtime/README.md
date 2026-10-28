# Live2D 本地运行时

固定入口为 `https://aipet.invalid/runtime/index.html`。页面不连接互联网，不包含模型包的 HTML 或 JavaScript；WPF 宿主负责用 WebResourceRequested 将资源路由到本机文件。不要将此入口改为模型自带网页。

## 运行资源

- `live2dcubismcore.min.js`：Live2D 官方 Cubism SDK for Web 5-r.5 原文件，未经修改。
- `renderer.js`：本项目 `../../live2d-web/renderer.ts` 与该 SDK 所附 Framework TypeScript 的离线 bundle。
- `shaders/`：该 SDK 所附 13 个官方 WebGL shader 原文件。
- `licenses/`：Core、Framework、SDK 原始许可说明、Core 再分发文件清单及官方协议文本快照。
- `provenance.json`：来源、SDK ZIP SHA-256、Core SHA-256 与实现约束。

此目录不依赖 CDN、npm 服务或本地 HTTP 服务器。`renderer.js` 使用静态 CSP，仅允许两个固定运行时脚本。模型资源通过 Fetch 按数据读取。

## 宿主协议 v1

导航前监听 WebMessageReceived，等待 `{type:"ready"}` 后发送：

```json
{"type":"load","url":"https://aipet.invalid/model/Hiyori/Hiyori.model3.json","requestId":"unique-id"}
{"type":"pause","paused":true}
{"type":"pause","paused":false}
{"type":"motion","enabled":false}
{"type":"motion","enabled":true}
{"type":"dispose"}
{"type":"stats","requestId":"probe-id"}
```

`load` 仅接受该固定源 `/model/` 下的 `.model3.json`。资源必须是包内相对路径；阻止跨源、绝对路径、上级目录、脚本扩展及查询参数。宿主仍必须对本地文件路径做规范化与根目录限制，并拒绝导航、弹窗、远程请求、非白名单资源和文件系统链接逃逸。

事件：`ready`（含 SDK/Core 版本）、`loaded`（含 requestId、drawableCount、textureCount、mocVersion）、`error`（含 requestId、中文 message）。初始化失败的 requestId 为空，宿主不能丢弃。可选 `stats` 与 `disposed`。

`dispose` 取消在途模型请求、清空透明画布并释放模型和模型 GPU 资源。页面自身的已编译 shader 保留，以支持再次 `load`；WebView 导航或销毁时释放 Framework 全局资源。切换 `load` 会中止旧请求，旧请求不再上报 success/error。

画布是真正 alpha 透明，按可见网格范围居中等比例适配并保留 3% 边距，像素比最大 2、最长边最大 2048。支持自动眨眼与呼吸时，稳定待机最高 30fps 绘制，尺寸变化会触发额外重绘；静止时仅按需重绘。暂停保留最后一帧并冻结动画时间，恢复时重绘挂起的尺寸变化，不追赶隐藏期间的时间。

1.2 用官方 CubismEyeBlink/CubismBreath 驱动真实存在且范围覆盖 0～1 的 `ParamEyeLOpen`、`ParamEyeROpen`、`ParamBreath`。不存在的参数不会虚构。`motion` 关闭时还原加载时默认参数并停循环；系统 `prefers-reduced-motion: reduce` 同样生效。宿主在模型加载前下发并缓存暂停和动画偏好。该功能不向模型写入网格或关键形。

不加载/播放模型包内 motion、expression、physics、sound，也不执行鼠标参数控制。可选 `.pose3.json` 仅在加载时用官方 CubismPose 初始化一次默认部件可见性，避免互斥手臂同时显示；不推进其时间。

`window.petDiagnostics()` 返回画布尺寸、透明/非透明像素数、模型 drawable 数、纹理数、帧数、暂停状态、GL 错误、实际像素边界。`opaquePixels` 表示 alpha > 0，`fullyOpaquePixels` 才表示 alpha = 255。
另含 `autoMotion`、`animationLoop`、`idle`；后者记录 enabled/reducedMotion/active 状态、驱动参数、当前值、默认值与已推进的动画秒数。

## 许可

开发与本机验证采用官方 SDK；这不代表获得任意形式的公开发行许可。Core 仍受 Live2D Proprietary Software License 约束，Framework 受 Live2D Open Software License 约束。对外发行前需根据官方 SDK Release License 与 Expandable Application 条款核对授权；可从文件导入任意模型的应用需特别核对该类别。官方入口：https://www.live2d.com/en/sdk/license/ 。

官方来源：https://www.live2d.com/en/sdk/download/web/ ，ZIP：https://cubism.live2d.com/sdk-web/bin/CubismSdkForWeb-5-r.5.zip 。
