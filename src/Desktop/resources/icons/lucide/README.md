# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录中的 `calendar`、`chevron-left`、`chevron-right`、`clock`、`moon`、`plus`、`radio`、`server`、`x`
即在该名单中，生成器会将 MIT 声明一并写入 `Lucide.js` 的文件头。
与上一级目录中的 SQUARE ENIX 游戏素材不同，这些文件**可以**随本软件公开分发。

| 项 | 值 |
|---|---|
| 上游发布 | npm 包 `lucide-static@1.55.0` 的 `icons/<name>.svg`，原样未改（首行保留上游的许可证注释） |
| 取用方式 | 开发期下载一次；运行时不联网 |
| 进入构建产物的形式 | 这些 SVG **本身不编入资源**。`tools/lucide-icons/generate.py` 将每个图标的绘制元素写入 `src/Desktop/qml/components/Lucide.js`（含 ISC 声明），QML 模块仅包含该文件 |
| 着色 | Lucide 的 SVG 使用 `stroke="currentColor"`，Qt 的 SVG 渲染器不支持该取值。`Lucide.js` 在运行时将按钮当前的文字颜色写入 `stroke`，并生成 `data:` 地址供 `Image` 使用 |
| QML 用法 | `AppButton { iconName: "plus" }`；或 `import "Lucide.js" as Lucide` 后 `Image { source: Lucide.source("plus", Theme.textPrimary) }` |

新增图标：从**同一版本**的 `lucide-static` 取 `icons/<name>.svg` 置于本目录，运行
`python tools/lucide-icons/generate.py`，并将新的 SHA-256 补入下表。`tools/lucide-icons/test_generate.py`
在 `Lucide.js` 与本目录的 SVG 不一致时失败，该测试由 `scripts/verify.ps1` 的工具自测执行。

| 文件 | SHA-256 |
|------|---------|
| `activity.svg` | `9cc7ea33e1c9f4f00b54c150807822a5ecceb178c5c7531ba31b3fbb84e94728` |
| `book-open-text.svg` | `c0dec0992ceabed00881322fce45bca5d63a87c9818796574c4a37a22edaebb7` |
| `calendar.svg` | `0fb29dc98bc17d84eadeba047341e9444edd2177604e80fce76f3f2f0663b80b` |
| `chart-column.svg` | `3e68b1a9191034eaf19175a1bc16138e7cc8274249dba44048299774b040c74f` |
| `chevron-left.svg` | `7cd7d32edf31996a69c66ba9515ed80a8ed1f6e69c2ef12672339f6fcaeb7041` |
| `chevron-right.svg` | `5498d2d7e15efeafe7d536450801e979b97cfc3bf0f1a5872eb40e5153d4b5ba` |
| `clipboard-check.svg` | `b2c9f28ddf01dbbf5042a3cc5c126a4c778de21209d3ae7ad7fdc83464bb0151` |
| `clipboard-paste.svg` | `880af1bd9689fcb1fd189be647227cc486d3ff26c3a5764642abfcfa12b1208e` |
| `clock.svg` | `3abefb4ed645ece77b115c5495693d8927328c3bd707a2dee3668a1adda4fb4c` |
| `file-check.svg` | `15f0924a47b65712655916113c14d17180de682ae83837c4ee5bc49d0aa4a82b` |
| `file-down.svg` | `381a423a1302d0bb757aa17ec8dd536a62330515c96d408f897df0aa322cea8c` |
| `file-input.svg` | `e42eb88d7975ef2d8799e723bc111009ed3b52f49688dbd2565bffaeb171711f` |
| `file-json.svg` | `6e6ae88e2d5ba4084d818e27d62d91bf3bf6cb82d81e3ebd5056dc46d8b5914c` |
| `filter-x.svg` | `5c2a3830a460b98bb20aa3aa50fc82765868783330f6232b4bcda84db33f4231` |
| `flag.svg` | `02edd5e48863749991b9698dfe31ffc224e1181aabf5976a1a71d9e7697d857f` |
| `folder-open.svg` | `87cb21914e600ea214e758199adbd382b6808c521ffda282f9d68fb0de0ab00e` |
| `gamepad-2.svg` | `4dd45a5a6f38fd24984a6dd5acd41882263f6b2e6cc393e8641b3538a8af1f11` |
| `history.svg` | `b45d03620b1e0af1c34004979915a37d2a00566680adbaa7d0645d8dcb8d71d6` |
| `image.svg` | `b357397c5bba97e1648bdd485a75bd6cfe7a14a2e277b13c8c270d33f7f0340b` |
| `image-down.svg` | `d63aabe083e22032b3687e9ce29d585cc5bbb3e87c5878b1e9e488d5634a982b` |
| `layout-dashboard.svg` | `f8e46ae1a32297a865eb4a96dbf60847030b3a2effa7657033a1401486c03cfd` |
| `moon.svg` | `d74fd34ca96abb76cbe78ce726933b26f2e988a0f1a651d7e976a30fda5d8bd2` |
| `network.svg` | `96e22e85e9d80a0de408c987b651be6e5ed94749c23b21e0da0c64b9210146fa` |
| `notebook-pen.svg` | `597f5235b9893dd7c11ac9873effe63bc922d4954edfd877008bf05cf5cd4479` |
| `plus.svg` | `573b8f5a3085beb0353f8c7b893fe1cecb9c0606a7be21fb473b93d75f84dc00` |
| `radio.svg` | `ec2f0b85898e4e0483e6482589945527e1fcd82d831551ca706e2b6f8c9afb9a` |
| `server.svg` | `f05702fe9ad28d8c6326c973673731386f5d0f4b75bc53e2f9291366078e1dde` |
| `settings.svg` | `4d6c0b1031459994a9db34f3caed818d69e39b3459ec65b4b9d7048f229280a1` |
| `sun.svg` | `b12898eac343189c8deccba77223aef1a5582ce69dba6e6b0c4bf8a9a24ae110` |
| `swords.svg` | `958c2ccec20293ebeda326de6dea2e22bfae63aeda33d80d7bf37e213542b7e3` |
| `users.svg` | `e9ff817cd8b9092f9fcce5267f4a1e779a536c17b3fa770f4aa8e5c1fb45c665` |
| `x.svg` | `65b78d9fa306e2b6386fb9f3a9f10e1ba8067fa0d079046e1050db84f18f31f6` |
