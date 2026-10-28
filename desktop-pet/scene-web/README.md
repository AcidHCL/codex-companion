# 动画播放器单元测试

生产播放器源码直接位于 `../assets/scene-runtime`，无需额外打包器。
在本目录用 Node.js 执行：

```sh
node --test test-motion-frame.cjs test-seamless-switch.cjs test-streamed-frames.cjs
```

这些测试使用固定模拟数据，不访问账号、模型或个人聊天记录。
原开发电脑专用浏览器验收脚本和生成的测试媒体、截图未纳入源码包。
