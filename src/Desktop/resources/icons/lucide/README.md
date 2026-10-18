# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录中的 `calendar`、`chevron-left`、`chevron-right`、`clock`、`moon`、`plus`、`radio`、`server`、`x`
即在该名单中，生成器会将 MIT 声明一并写入 `Lucide.js` 的文件头。
与上一级目录中的 SQUARE ENIX 游戏素材不同，这些文件**可以**随本软件公开分发。

| 项 | 值 |
|---|---|
| 上游发布 | 官方仓库标签 [`1.59.0`](https://github.com/lucide-icons/lucide/tree/1.59.0/icons) 的 SVG；按同标签 `packages/lucide-static/scripts/copyIcons.mts` 添加版本许可证注释与 `class`，绘制元素原样保留 |
| 取用方式 | 开发期下载一次；运行时不联网 |
| 进入构建产物的形式 | 这些 SVG **本身不编入资源**。`tools/lucide-icons/generate.py` 将每个图标的绘制元素写入 `src/Desktop/qml/components/Lucide.js`（含 ISC 声明），QML 模块仅包含该文件 |
| 着色 | Lucide 的 SVG 使用 `stroke="currentColor"`，Qt 的 SVG 渲染器不支持该取值。`Lucide.js` 在运行时将按钮当前的文字颜色写入 `stroke`，并生成 `data:` 地址供 `Image` 使用 |
| QML 用法 | `AppButton { iconName: "plus" }`；或 `import "Lucide.js" as Lucide` 后 `Image { source: Lucide.source("plus", Theme.textPrimary) }` |

新增图标：从**同一版本**的 `lucide-static` 取 `icons/<name>.svg` 置于本目录，运行
`python tools/lucide-icons/generate.py`，并将新的 SHA-256 补入下表。`tools/lucide-icons/test_generate.py`
在 `Lucide.js` 与本目录的 SVG 不一致时失败，该测试由 `scripts/verify.ps1` 的工具自测执行。

本次采用官方标签源码获取资源，并逐个核对 Git blob SHA。上游别名保持与 `lucide-static`
发布流程一致：`file-json` 来自 `file-braces`，`filter-x` 来自 `funnel-x`，`history` 来自
`rotate-ccw-clock`；别名关系由同标签的对应 `.json` 文件声明。

| 文件 | SHA-256 |
|------|---------|
| `activity.svg` | `bdd922ab19627b3e13fb147cc1d0de77c83de86fb6a27327990bbf4e44b399c4` |
| `book-open-text.svg` | `74933dc151295d468ce3a8f7d074e3ff85255c0aeae329b483a21ba8c20ecfdb` |
| `calendar.svg` | `7ce62f16c86c4af97e633f5073bb0c6082b5b52c3c0c7dfc64b2eb9a7ac5cd29` |
| `chart-column.svg` | `850130cd64f610427bcadca0edce9513c49f4c520dbf7c05a90f0b8278d504d8` |
| `chevron-left.svg` | `55855ba9ca6bd3f350d1f6e092f669518679d1c86a52607a1bd2640cbf5af31e` |
| `chevron-right.svg` | `b9e4521dff43288a385af1421b625da8fd40cf28c272a508c933217002090776` |
| `clipboard-check.svg` | `db363fb803050c9cd521a13bfe8754a57849c69ee98f8ac0bf748c0cd98caf50` |
| `clipboard-paste.svg` | `174e08f71d4ea5e1941f4eb77a1918fe1d0a20e73ac8ac51a21e86afe99dd8f9` |
| `clock.svg` | `5d880dd7871051fbed532ed729ee55726c9aee58f13a3218f9b6a92a19393ba3` |
| `file-check.svg` | `deadd8b64349a21b8bc64bdb50ebfa7d75f145a142f6e6b0c163862e377afba3` |
| `file-down.svg` | `b19aa9f0caf51abd0bddfe111072d1f77a1adb2ce53eed16b7f59027ec6a7187` |
| `file-input.svg` | `bee6c9adb0f6d8e103356e7996c1fac2af22b2c1da0b4f8efdf404c6b097808d` |
| `file-json.svg` | `105996f533ed96f4c3712e25640df239f439cd3da00c2adc1416700aaeb3a4c2` |
| `filter-x.svg` | `4ffe09ec7010ef5dd9da60603f7cf2a8d6fd70381386d1d461ec94c29c9788a5` |
| `flag.svg` | `2377c170cdcdcdc5ba897ffe3a67869634618637d2bd76d5c7ebc99e742dab81` |
| `folder-open.svg` | `a2efa421fdf9310997f603008fc62ab4876da17a2acda74ae3707b938039090b` |
| `gamepad-2.svg` | `1c1624bac10abf9f6feb2dc9d64f2c7b1fd2d906d4072ae165942db0464866bc` |
| `history.svg` | `2cc779bfbb62de9870eaa619254329c4fccf5305f8a3582eded7b69a8ddfd0c9` |
| `image.svg` | `bd33f9ce149098d4729669e62e2c3ceb042083a3f294f51deaa54f92ef01c7bc` |
| `image-down.svg` | `77427911ac25af09e930d8fb74a3f57652ba8fee0e58e06734cfd739b95b32a5` |
| `layout-dashboard.svg` | `3e8ff4747286e247f64c3395fc13bb1316f0d399300cee56c27be8d79fc778f0` |
| `moon.svg` | `811e53fbfe9572174588012102712d6b9135a21cc19569afcef6d2e226958142` |
| `network.svg` | `5bd826c6d762aedf3787a5d19a572630dda1c4cca7d832cae44e7efee41543a8` |
| `notebook-pen.svg` | `b8f46e9b1444331b13ca2fbbffda2f67199d35ae3b50c8db631a7adc2964ff27` |
| `plus.svg` | `faf0bab01219d720cdd3047f6b5755c78c8e5a6743008a8fcd1c48fec4879519` |
| `radio.svg` | `ac08d9851e28e181432cd2ec342835fb8534fa3386a032cb0d45b16aa65ae17a` |
| `server.svg` | `bd6b00a7b40f241c6c4026b8994a0b1575a8654ec061d965f506a9ceaf0195e6` |
| `settings.svg` | `c23eb8c13e7ee68672cca78ff5176f6fa89238ae5c0eec960a2e1e161e8d25cc` |
| `sun.svg` | `e00cbd74f9cf7b463d45f4980388d745095cea2188a1729d734b80fc5055187d` |
| `swords.svg` | `676708455d756090e921f96d0a846abc54c4de49c887fd64b6161b6c04a3eaee` |
| `users.svg` | `a005a3af8fa3d1a9f5ded9a62c5ecc6c070d16308cffc0c81fa5b40282b1a4f2` |
| `x.svg` | `b22c83fd632ff4082021e34d64b1393591134e7ce2889a6dad5361eff2fcc716` |
