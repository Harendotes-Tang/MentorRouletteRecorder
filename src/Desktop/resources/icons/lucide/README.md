# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录中的 `calendar`、`chevron-left`、`chevron-right`、`chevrons-down`、`clock`、`moon`、`plus`、`radio`、`server`、`target`、`x`
即在该名单中，生成器会将 MIT 声明一并写入 `Lucide.js` 的文件头。
与上一级目录中的 SQUARE ENIX 游戏素材不同，这些文件**可以**随本软件公开分发。

| 项 | 值 |
|---|---|
| 上游发布 | 官方仓库标签 [`1.61.0`](https://github.com/lucide-icons/lucide/tree/1.61.0/icons) 的 SVG；按同标签 `packages/lucide-static/scripts/copyIcons.mts` 添加版本许可证注释与 `class`，绘制元素原样保留 |
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
| `activity.svg` | `ad2e728ce1a3bf4dee93223ce3912c7b52cd2ee061c832e08ba0a14b028f68ee` |
| `book-open-text.svg` | `7a6234f84d2f296421c4e27ea6d885e18d45bcb7737b5aa36a4ec6c147b0f746` |
| `calendar.svg` | `5c0372f26e27a19c72b51e27315fa57d0dddbc325c6a2bf0ec32218e3a542ef6` |
| `chart-column.svg` | `e4104761d3c5e4423544563d0e198550fbc301d43895db91703754d1e1a2bc13` |
| `chevron-left.svg` | `6e7478a72752c404c2ff077dfb768a84b718255d7bf9ea547de9b080114592ee` |
| `chevron-right.svg` | `2af3089fa09497d5725172b172196d193cbe8205bf70ad6f9078ce4773285001` |
| `chevrons-down.svg` | `92c65689ea0c942b24b9e675ac0f435c92e1d8fcdc0937eeb472a082ed81e38a` |
| `clipboard-check.svg` | `4ac5ba3c2308b50d29c6cd5ba499166dc45ac433d355041eebd38a985197d6d9` |
| `clipboard-paste.svg` | `7a780940d3b4f6009d6ea174db51521a8bac9bdfbc0e8c16be41cb6d1b476723` |
| `clock.svg` | `2a50ff1030ae5591447dd5b278bdbd24e7dda3c24e2133348e7a66231fd6e787` |
| `file-check.svg` | `dacdcd0514471272bb11c18769f42399dd068ff52151030b0b0a8463a8262945` |
| `file-down.svg` | `1256558eaa140c1ac65b3f6cf1291ae5898a79ebe9ca5cc23873d45e4551216f` |
| `file-input.svg` | `8b978cfd489cd92ca0ce4d0303bd0729cf9700ecee77daaf004b356440d6fd46` |
| `file-json.svg` | `6a27ab5aa7f7960053588d374423bd56e4edc57039345f8c59bf1ee332f1379e` |
| `filter-x.svg` | `abc4f423c1281e2e5080c3bcb95918a11c3d2a4870bca0eb8a44d9bdc9d57651` |
| `flag.svg` | `e84d0cd56503debfaebf1f6c1c587cae253db067c1ff69a0737e2c4dd5296398` |
| `folder-open.svg` | `5adf2987a25d174cb7c0ecfd241f12c40e35953217d7c4ee5ced8d29a3baef6f` |
| `gamepad-2.svg` | `0a25090b8e0cf03d14a58e3fe5e85335402485c58c610034e64eb73c2c70241a` |
| `history.svg` | `15a0aa9c5c97acef0295b80e9d6a7d1d34f1e82f85c9f2866fc78187bce4d0d8` |
| `image-down.svg` | `add0e62da3c12f3a04013d5cda5ea0d85e9019a23c06d7e3c68ca0bd102e0265` |
| `image.svg` | `eb8267cf5cd811745284486e5494d022c37c0fb7cfc3e0c3c543675c3268db13` |
| `layout-dashboard.svg` | `b02f45800b01e728a5f1a0c9eb2a7f7a12fef43917f2f5bda41259779e95b364` |
| `lock-open.svg` | `7a49019d298ff9aedd0b915df02a7f0ba65650bea2f100e9b5c0cc33d8453113` |
| `moon.svg` | `3002badbef72a8f85358b3fe844ccac9c6e9638cc8088fb197426e8aeee6e0a9` |
| `network.svg` | `4f2d416762604ac95e05546c074635bfaa24346680fc9d938f6849da2ce92f17` |
| `notebook-pen.svg` | `119524879d2fc8c753887114ab988ee8f788482f2afddfb3103cc21d6d17cf86` |
| `plus.svg` | `dfcf07b39b0f4c0b422595ae488fb08c468ef4782f6ef37d0deceaadfb2a5693` |
| `radio.svg` | `8637908d6b1f5172ba710b9e12c052f614e2facdfc66535f0eb60d9a8a0ced6c` |
| `server.svg` | `91feaa119e863973e4be3a2beab09e7b21944bbe508d12b5268d903b757177f8` |
| `settings.svg` | `f2235dfd6c69bdad1acd08554ba19557becefa526b21ae4cfabf8fe7bbc48796` |
| `sun.svg` | `19d684776d546b673d45fc54b7dee7b22f18e0b2e673612220727e90f46fb4cf` |
| `swords.svg` | `990cb2394a31acb9ed6fa898746d9e9c05bd199fe6342d6b95cb206f25e43e59` |
| `target.svg` | `e26469cfee68a31c0ab6712987f7d05ea5d5326cb7b7061a243369e5e25c7d15` |
| `users.svg` | `8d17ead4eb85514d53a2aa1b6e04198a4270207f2266fef9356967964db91f1e` |
| `x.svg` | `a611112e8946480120e5e96f1d849651df76fa502f6190e293defaf6c4f56b07` |
