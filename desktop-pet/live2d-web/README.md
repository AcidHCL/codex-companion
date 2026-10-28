# Live2D 前端源码

应用直接使用 `../assets/live2d-runtime` 中的运行资源，普通 .NET 构建无需 Node.js。
修改 TypeScript 后，在本目录执行：

```sh
pnpm install --ignore-scripts --frozen-lockfile
pnpm exec tsc -p tsconfig.json
node build.cjs
node --test test-standard-idle.cjs
```

`vendor/Framework` 保留官方 Cubism Web Framework 源码与许可证。
Core 是单独分发的原版运行库，不应重新 bundle 或删除其许可证。
此源码包未包含原开发电脑专用的浏览器验收脚本、截图和报告。
