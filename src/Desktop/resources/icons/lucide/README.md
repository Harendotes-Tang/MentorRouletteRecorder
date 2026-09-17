# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录中的 `calendar`、`chevron-left`、`chevron-right`、`clock`、`moon`、`plus`、`radio`、`server`、`x`
即在该名单中，生成器会将 MIT 声明一并写入 `Lucide.js` 的文件头。
与上一级目录中的 SQUARE ENIX 游戏素材不同，这些文件**可以**随本软件公开分发。

| 项 | 值 |
|---|---|
| 上游发布 | npm 包 `lucide-static@1.46.0` 的 `icons/<name>.svg`，原样未改（首行保留上游的许可证注释） |
| 取用方式 | 开发期下载一次；运行时不联网 |
| 进入构建产物的形式 | 这些 SVG **本身不编入资源**。`tools/lucide-icons/generate.py` 将每个图标的绘制元素写入 `src/Desktop/qml/components/Lucide.js`（含 ISC 声明），QML 模块仅包含该文件 |
| 着色 | Lucide 的 SVG 使用 `stroke="currentColor"`，Qt 的 SVG 渲染器不支持该取值。`Lucide.js` 在运行时将按钮当前的文字颜色写入 `stroke`，并生成 `data:` 地址供 `Image` 使用 |
| QML 用法 | `AppButton { iconName: "plus" }`；或 `import "Lucide.js" as Lucide` 后 `Image { source: Lucide.source("plus", Theme.textPrimary) }` |

新增图标：从**同一版本**的 `lucide-static` 取 `icons/<name>.svg` 置于本目录，运行
`python tools/lucide-icons/generate.py`，并将新的 SHA-256 补入下表。`tools/lucide-icons/test_generate.py`
在 `Lucide.js` 与本目录的 SVG 不一致时失败，该测试由 `scripts/verify.ps1` 的工具自测执行。

| 文件 | SHA-256 |
|------|---------|
| `activity.svg` | `46a64a16f5f773b31bdfd64f42930aaf0952bb4f90e59dc9eb3d5cbce4372117` |
| `calendar.svg` | `53a4c32c41c0b9e52c14c64453eb29aa38584f7fa642ceca288169144c197c7b` |
| `chevron-left.svg` | `d5a124b49b704aa914363bab19f4f7213258d8e1e62100627a0ecaa4cb3d2d3a` |
| `chevron-right.svg` | `3abb8adc7fc16fee93fb45e817cacf63b326d765a390a9202333c692f59ca06a` |
| `clipboard-check.svg` | `e83569fac42564b6ea8fd245d1aba145b7975b870677dfb214dd603c585cd4e1` |
| `clock.svg` | `35d53f97cd90c61d03dc44dc3066e18459b40452710252005e0680d8e7743c17` |
| `file-check.svg` | `4522b2a4cadcc728464fa9a7862144c1d8cfc09c9788e5d8ee6fce85ae63ce56` |
| `file-down.svg` | `16c8a9587341cc49d3a78a1f627fdd7f7b4a39bab44d1fcd926d0037154c2ea2` |
| `file-json.svg` | `9264dc95815e7254a7b5971f39b95a6fda1d409b2f42209ec53df468a2ccd920` |
| `filter-x.svg` | `e4df6a2ec606e9ad087c3a39ece8f703f437a929df6ee3a66a708e844a3c5f38` |
| `flag.svg` | `ad8bb75e072b630f06522e8c837cbe27202bf3355c2f15014f398ccb557d606b` |
| `gamepad-2.svg` | `c6657f1b594ec163aeb812c6ebb0e84a3fbe303662f3934730a5527377613338` |
| `history.svg` | `fa003df2e3e870987f85934a1428e2db2aaa0ebfa9eea22955595b6795d25c4c` |
| `layout-dashboard.svg` | `3f40ee77c84e4d2176e2bc2a6315eb54893f80cb430c669d285f53cd5047fd4c` |
| `moon.svg` | `5e34800d69c624984af52ee10097a03f7dc8e3bc2dae18215914f675abdad514` |
| `network.svg` | `54b53adf698a105bf6ccf0d767908b7f22179705f963af39e11720565f9dec3d` |
| `plus.svg` | `f729556dad8f5316a6419a5f44ade394885657fab1ca2908ee80dcd5afe81616` |
| `radio.svg` | `1c83801980a3cdbb0b31f19509331f960ebe1974b7e21557b5116a658b8d946c` |
| `server.svg` | `cbd7f3b900551c67d02e803ba49d9f1eb85b3de8fd07ed321e5e54fe1d740808` |
| `settings.svg` | `0ada2477cd92fa2c451d2a776efdd9f61206d2b30bf982b4f92980db709bd4b1` |
| `sun.svg` | `0c22918e36080bc46e6d96750afc8490b7de945f40de8bb31448931a9bc0d1e0` |
| `swords.svg` | `15d19bf173849e750bfbadf57794251baee085fa21bf1799a031ff00c2356844` |
| `users.svg` | `978d9a4f4cfdd415f0b3bab3adca648155c91483f850679d8829aa04208b43e1` |
| `x.svg` | `7d168da01aba19d3ecdae6abbcc2c6ffc8a1314499178307cfa4beae26794d59` |
