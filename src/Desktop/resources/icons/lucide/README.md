# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录中的 `calendar`、`chevron-down`、`chevron-left`、`chevron-right`、`chevrons-down`、`clock`、`globe`、`moon`、`plus`、`radio`、`search`、`server`、`target`、`x`
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

原有图标采用官方标签源码获取资源，并逐个核对 Git blob SHA。上游别名保持与 `lucide-static`
发布流程一致：`file-json` 来自 `file-braces`，`filter-x` 来自 `funnel-x`，`history` 来自
`rotate-ccw-clock`；别名关系由同标签的对应 `.json` 文件声明。

2026-11-09 新增 `search`、`globe`、`chevron-down`，直接取自固定的官方 npm 包 `lucide-static@1.61.0`（`https://unpkg.com/lucide-static@1.61.0/icons/<name>.svg`），保留上游 SVG 及版本注释，校验值如下。


2026-11-09 图标统一补齐：复选、浮窗、完成状态、窗口控制与复制操作的新增图标同样取自上述固定官方 npm 版本；浮窗旧名称由 `OverlayIcons.js` 映射到标准 Lucide 名称，运行时仍只读取本地生成库。

| 文件 | SHA-256 |
|------|---------|
| `activity.svg` | `ad2e728ce1a3bf4dee93223ce3912c7b52cd2ee061c832e08ba0a14b028f68ee` |
| `arrow-down.svg` | `e4aef5810b8e5e1304c735f2af5c6f7c4cdebad1e00b4b2ebbf2e1bdfea17528` |
| `arrow-right.svg` | `78af91b2c9d7cf8ea9564c3ca28a4f9021cced57aee0bc3bc801d68555d5b8e1` |
| `arrow-up.svg` | `f76f6cc5e50fe25865dbf403a816016b1cf8d4de1bb4e230ff5b541b69bcde34` |
| `bell-off.svg` | `43acc0c96240dd3ef1abf36ecfdffc262921524832378d44a904f78c9cc979e4` |
| `bell.svg` | `01f419f823d3332da275a17fc7fcac7997597a0f533c9c81678ef5ee5b7bc341` |
| `book-open-text.svg` | `7a6234f84d2f296421c4e27ea6d885e18d45bcb7737b5aa36a4ec6c147b0f746` |
| `calendar.svg` | `5c0372f26e27a19c72b51e27315fa57d0dddbc325c6a2bf0ec32218e3a542ef6` |
| `chart-column.svg` | `e4104761d3c5e4423544563d0e198550fbc301d43895db91703754d1e1a2bc13` |
| `check.svg` | `cf5d8f45a017604917c0d74ed9c49c6926b0df780c4c9ad3ad85fb7a392bf746` |
| `chevron-down.svg` | `763bbc7f305bad8b852b5ae5feadfc08858acf97f19a5897aa8f1fe636abed45` |
| `chevron-left.svg` | `6e7478a72752c404c2ff077dfb768a84b718255d7bf9ea547de9b080114592ee` |
| `chevron-right.svg` | `2af3089fa09497d5725172b172196d193cbe8205bf70ad6f9078ce4773285001` |
| `chevrons-down.svg` | `92c65689ea0c942b24b9e675ac0f435c92e1d8fcdc0937eeb472a082ed81e38a` |
| `circle-alert.svg` | `9297d24761982135593d8a24fa90f3b901e36ec3b79a744df61012b4bb74addc` |
| `circle-help.svg` | `2617f32e3ffe4a0591a0d57f21f1c67fda961b4f15f779ecfe246a09593f9ae0` |
| `clipboard-check.svg` | `4ac5ba3c2308b50d29c6cd5ba499166dc45ac433d355041eebd38a985197d6d9` |
| `clipboard-copy.svg` | `7ee8d22109aa35b8d02294b91ba89fcfbfc6046f6bd452aa99a55c2e855d13ad` |
| `clipboard-paste.svg` | `7a780940d3b4f6009d6ea174db51521a8bac9bdfbc0e8c16be41cb6d1b476723` |
| `clock.svg` | `2a50ff1030ae5591447dd5b278bdbd24e7dda3c24e2133348e7a66231fd6e787` |
| `cloud-off.svg` | `1d604c85417975248f5378283560df9668aa9d0a474edcf74d0dc6b69e1c3659` |
| `copy.svg` | `0e68d3e49db956dfe42e8dfac1d8596081ee0a6fc5480fc6318d231bb07616d9` |
| `diamond.svg` | `d4fd83ee244b877954f1de15c802a752ee41a33d7daa28b382a2e1da5c5eb984` |
| `file-check.svg` | `dacdcd0514471272bb11c18769f42399dd068ff52151030b0b0a8463a8262945` |
| `file-down.svg` | `1256558eaa140c1ac65b3f6cf1291ae5898a79ebe9ca5cc23873d45e4551216f` |
| `file-input.svg` | `8b978cfd489cd92ca0ce4d0303bd0729cf9700ecee77daaf004b356440d6fd46` |
| `file-json.svg` | `6a27ab5aa7f7960053588d374423bd56e4edc57039345f8c59bf1ee332f1379e` |
| `filter-x.svg` | `abc4f423c1281e2e5080c3bcb95918a11c3d2a4870bca0eb8a44d9bdc9d57651` |
| `flag.svg` | `e84d0cd56503debfaebf1f6c1c587cae253db067c1ff69a0737e2c4dd5296398` |
| `folder-open.svg` | `5adf2987a25d174cb7c0ecfd241f12c40e35953217d7c4ee5ced8d29a3baef6f` |
| `frown.svg` | `455c974a8e9ae585907525a8ab3d17508f3bb3da3c901e0be0fb7a3c29a38788` |
| `gamepad-2.svg` | `0a25090b8e0cf03d14a58e3fe5e85335402485c58c610034e64eb73c2c70241a` |
| `globe.svg` | `994924c198fe332f0de7eb5953610c6bce145b93db86c97d7183ce170a6038cc` |
| `grip-vertical.svg` | `3a3be2604ef99b245d34f135139dd8cf91c1d4078d6660a48c01a4660cd987cf` |
| `hard-drive.svg` | `86c8c22e2e65cb913348935228c857de676f658cae3eb9acda7b79de53168bc6` |
| `history.svg` | `15a0aa9c5c97acef0295b80e9d6a7d1d34f1e82f85c9f2866fc78187bce4d0d8` |
| `hourglass.svg` | `ed5ef04959d0ec3fbd7c05a730e67265c0e6626746952904fb109a5a598d7475` |
| `image-down.svg` | `add0e62da3c12f3a04013d5cda5ea0d85e9019a23c06d7e3c68ca0bd102e0265` |
| `image.svg` | `eb8267cf5cd811745284486e5494d022c37c0fb7cfc3e0c3c543675c3268db13` |
| `inbox.svg` | `311abdf01ec9fe0ecd9b2a021d544b3ae2c208c0481c283837f682d836073e52` |
| `info.svg` | `2dbeb8e85ec1aae8eedea831deb24a31b04917bceae5ce63bd49eaf07fdaf425` |
| `layout-dashboard.svg` | `b02f45800b01e728a5f1a0c9eb2a7f7a12fef43917f2f5bda41259779e95b364` |
| `lock-open.svg` | `7a49019d298ff9aedd0b915df02a7f0ba65650bea2f100e9b5c0cc33d8453113` |
| `lock.svg` | `d5b171fc7578184bf9c70b20202e1398afeea956ca07bc896a8b88c23f1041e6` |
| `log-out.svg` | `db1bc9f72f1bf537718991c9ab03bf8d913c4e67a820335d4f992cc97d18b65a` |
| `maximize-2.svg` | `ee10a7d2d22d1eb81e26dbe21e4428f6afde774d3ec9d2b350c5832b51cea02b` |
| `medal.svg` | `1bb9bfd648a2870f5a596315e583ceae0aaad5e122a8ca465fd1c4ded12cc607` |
| `meh.svg` | `6a4d09e88996e895766f6a404223e19d2996b5b57d4e51cabaddeac34a62eaf5` |
| `minimize-2.svg` | `002f219c062f0fd41d5cc402b5b606c286c5a90aa9f7267486d9b3a8dc51b802` |
| `minus.svg` | `98fa3350421dfd2011b2f109d20de6069c963b1f668c24fe675878b8a4c86532` |
| `moon.svg` | `3002badbef72a8f85358b3fe844ccac9c6e9638cc8088fb197426e8aeee6e0a9` |
| `move-diagonal-2.svg` | `98359ae37dfb6df7fe8b1c434f5bd049dbd6cc3b5e3c6a770c8bfa5f9d83439f` |
| `network.svg` | `4f2d416762604ac95e05546c074635bfaa24346680fc9d938f6849da2ce92f17` |
| `notebook-pen.svg` | `119524879d2fc8c753887114ab988ee8f788482f2afddfb3103cc21d6d17cf86` |
| `panel-top-close.svg` | `5bed49ff66484217b7f685eead046214af488425a0824d364be48cf1bd45c4ac` |
| `plus.svg` | `dfcf07b39b0f4c0b422595ae488fb08c468ef4782f6ef37d0deceaadfb2a5693` |
| `radio.svg` | `8637908d6b1f5172ba710b9e12c052f614e2facdfc66535f0eb60d9a8a0ced6c` |
| `search.svg` | `e0d89f53155655032f5c07fd8da930800b62b69a778d1dd76b388b9b78bf1d3f` |
| `server.svg` | `91feaa119e863973e4be3a2beab09e7b21944bbe508d12b5268d903b757177f8` |
| `settings.svg` | `f2235dfd6c69bdad1acd08554ba19557becefa526b21ae4cfabf8fe7bbc48796` |
| `sliders-horizontal.svg` | `d2b52bb681ecad5c0710c4bba8684fd7be8dfe1d2bf3c10e865dff242cf3775f` |
| `smile.svg` | `703de8784f19c79d5768b8ed69c9bf6296d82089e7f9cc63513e278c17b72f42` |
| `square.svg` | `3855d336619de5adc61a0b5293db82cb9f5456b859a7daf0cd249653c6f31063` |
| `sun.svg` | `19d684776d546b673d45fc54b7dee7b22f18e0b2e673612220727e90f46fb4cf` |
| `swords.svg` | `990cb2394a31acb9ed6fa898746d9e9c05bd199fe6342d6b95cb206f25e43e59` |
| `target.svg` | `e26469cfee68a31c0ab6712987f7d05ea5d5326cb7b7061a243369e5e25c7d15` |
| `timer.svg` | `706c74414d251d9faabb039ce9205bbb0e8c4b61181db439e4514672c895b58d` |
| `users.svg` | `8d17ead4eb85514d53a2aa1b6e04198a4270207f2266fef9356967964db91f1e` |
| `x.svg` | `a611112e8946480120e5e96f1d849651df76fa502f6190e293defaf6c4f56b07` |
