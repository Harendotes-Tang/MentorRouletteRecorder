# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录中的 `calendar`、`chevron-left`、`chevron-right`、`chevrons-down`、`clock`、`moon`、`plus`、`radio`、`server`、`x`
即在该名单中，生成器会将 MIT 声明一并写入 `Lucide.js` 的文件头。
与上一级目录中的 SQUARE ENIX 游戏素材不同，这些文件**可以**随本软件公开分发。

| 项 | 值 |
|---|---|
| 上游发布 | 官方仓库标签 [`1.60.0`](https://github.com/lucide-icons/lucide/tree/1.60.0/icons) 的 SVG；按同标签 `packages/lucide-static/scripts/copyIcons.mts` 添加版本许可证注释与 `class`，绘制元素原样保留 |
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
| `activity.svg` | `c857ff3d5d800e04001911ad1dafffcf2f47b6f87edaf7c92e14643610caba0d` |
| `book-open-text.svg` | `5dd4db922f660fe6194c8de130bdd2bbcb1361b6e1e9995e8c08ae4fa55687cf` |
| `calendar.svg` | `6550e4e31fd7d5c4ad494c0799fe86f92db97ed1ba37b89ba98b04ff1560e546` |
| `chart-column.svg` | `a6480a03a116883dc6e97413945010dfba3c36492e1fe652f1c25e397d554be0` |
| `chevron-left.svg` | `5a22cb1c8927bf7f348fa84a931368df6682832ace8504716b4d421ea271e6e4` |
| `chevron-right.svg` | `fc318a364fbba06b47e6472ed38a088c29c9b5d9cee2247fb12bdeb9e87a042b` |
| `chevrons-down.svg` | `dc815d1336036d7229323f59411e96bb192c823ab0b028fd7694fd7272907cd5` |
| `clipboard-check.svg` | `dffc220ce1459d141e7c3a8ee5d2d233ca3f46f23ca1c9e48db495e54467818b` |
| `clipboard-paste.svg` | `173c98094720467d0b7068cdfe59e879ff9de3c1859dc07fcdaee2be5844e3ae` |
| `clock.svg` | `ec8c07b897a0d6ab2ad8a20538af6d0a7b1229a75fe9c34947b6033cd2541ad7` |
| `file-check.svg` | `b2f7ffac3f303a090fe3af9d9b2469dd10d3b79cae4a3f3731890417508f1c51` |
| `file-down.svg` | `0dd94c3d3b4514cb1c4ff2252f87838186a06848b2f586482c6d409018846271` |
| `file-input.svg` | `4f1abf6c3ed3c6472f76c2c33ce0b8c9e7e1d8bb157930df1ddbc0483133b722` |
| `file-json.svg` | `996e2d875ff219f72533ce346898555c1e64b73cb6a73c0bae1f777c52deb762` |
| `filter-x.svg` | `3ef678a80115579d4ac3747ce698b6bc30bfbd5b4b568fe1882f572befd52612` |
| `flag.svg` | `1e01dde15266e3d738eb178a4b2fd45c5cbcb2a4b40b02979069580e9d350f4a` |
| `folder-open.svg` | `08d5439a88a0fc520c369f313eedd0c2d2efc28594c431bf460ac472159485d2` |
| `gamepad-2.svg` | `42efdaceada98abe0b357d362d89850565e7f96ab70ea522d57443a3b3f9b44c` |
| `history.svg` | `ec796c85d8606afc95995ccf7454eb6621a4f79e551fd6beb81696ced0f670e2` |
| `image-down.svg` | `649a1b4db02c517e38a4903e81d569f31504f418a13654b0507955370837dada` |
| `image.svg` | `58b6309353bdd4838ec7ff612f2a4ccee68a7c655e5d735e6751eeae8a722fce` |
| `layout-dashboard.svg` | `f7270ead45b34f3296065233b5abf136e9ecd4c3e40f644ae8fab94063665d52` |
| `lock-open.svg` | `9dbc45081ea012e0f4f93de2e428b9262a2e694c821da05a9300f087977cdf20` |
| `moon.svg` | `5f2f11014bc4497c0858ad35137f989906039f01f30667755f69be5be631910b` |
| `network.svg` | `adda9c08b49e2e306d80b303c3c61065c3df6d2938b96b0e28a743cb768a957d` |
| `notebook-pen.svg` | `79f28255c013e3fa48abb1014f2c55a3191a6f0023ca0c4ef1c9028d0a0b75b7` |
| `plus.svg` | `6daef8329169d1c6216922e8e3e1587facb9729c1e8a9b939f2b3ec469025caf` |
| `radio.svg` | `fb6927e62e958fca73a4e81250d6aaad95249135bcf5aebb5443570378806d00` |
| `server.svg` | `10f4dad75633c5ac67b40adb201e8a75e72c649dd29115ef86e8457078c27d1b` |
| `settings.svg` | `4615a0b6ad349e8d2c905b0a195d01024a2e482d2483a0d5111df4d44c52ba0f` |
| `sun.svg` | `04cdfe15f35c2aeaf71f04a8aa3417e1d50c8c5ec507947cd0a9991e3ca02367` |
| `swords.svg` | `74075716a4e986e5866444008c5ac9b5dc95dce95a5c3d303bedc02281814a17` |
| `users.svg` | `2c3cb6b0e1d67b85d4359271d51596b1e1d32164aeb821bca96ded9caf72dd87` |
| `x.svg` | `c9adc6f731c23c2d505948a9c7b3fd9fa1bf8e41144d7842553241215920a636` |
