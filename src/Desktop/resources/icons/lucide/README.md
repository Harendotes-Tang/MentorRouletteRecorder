# Lucide 图标 / Lucide icons

按钮、侧栏导航与状态板上的线条图标来自 **Lucide**（<https://lucide.dev>，仓库 <https://github.com/lucide-icons/lucide>），
许可证 **ISC**（全文见同目录 `LICENSE`，即上游 `lucide-static` 包内的 `LICENSE`）。
`LICENSE` 的后半段列出了 Lucide 从 **Feather** 项目派生的图标，它们同时受 **MIT** 许可证
（Copyright (c) 2013-present Cole Bemis）约束。本目录全部 82 个图标中有 29 个属于该名单：`arrow-down`、`arrow-right`、`arrow-up`、`calendar`、`check`、`chevron-down`、`chevron-left`、`chevron-right`、`chevrons-down`、`circle-alert`、`circle-help`、`clock`、`external-link`、`frown`、`info`、`lock`、`log-out`、`meh`、`minimize-2`、`minus`、`moon`、`plus`、`radio`、`search`、`server`、`smile`、`square`、`target`、`x`。
其中 `circle-alert` 与 `circle-help` 在名单上仍写作 Feather 的旧名 `alert-circle` 与 `help-circle`（Lucide 后来改了名，对应关系记在生成器的 `FEATHER_RENAMES` 中）；`globe` 不在名单上。
生成器按同一份名单把 MIT 声明与这 29 个名称一并写入 `Lucide.js` 的文件头（`fromFeather`）。
与上一级目录中的 SQUARE ENIX 游戏素材不同，这些文件**可以**随本软件公开分发。

| 项 | 值 |
|---|---|
| 上游发布 | 官方仓库标签 [`1.76.0`](https://github.com/lucide-icons/lucide/tree/1.76.0/icons) 的 SVG；按同标签 `packages/lucide-static/scripts/copyIcons.mts` 添加版本许可证注释与 `class`，绘制元素原样保留 |
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


2026-11-19 全部 69 个图标升级到 `lucide-static@1.76.0`（`https://unpkg.com/lucide-static@1.76.0/icons/<name>.svg`），逐个覆盖并重新生成 `Lucide.js`；与 1.61.0 相比只有 `circle-help` 的问号画法有变化，其余文件仅版本注释不同。下表为新文件的校验值，上文两处「1.61.0」记录的是当时的取用版本。

2026-11-23 新增 `languages`、`send`、`skull`、`star`（「提醒与宏」的语言、投稿、BOSS / 阶段与收藏），同样取自 `lucide-static@1.76.0`，校验值已并入下表。

2026-11-23 新增 `external-link`（「提醒与宏」中打开浏览器的按钮用它代替文字箭头「↗」），同样取自 `lucide-static@1.76.0`，校验值已并入下表。

2026-11-25 新增 `layers`、`asterisk`、`refresh-cw`、`arrow-up-down`、`rows-3`、`chevrons-down-up`、`chevrons-up-down`、`shield-check`（「提醒与宏」副本目录的「全部」「通用」、刷新共享库、排序、紧凑行高、全部收起与展开、已核验），同样取自 `lucide-static@1.76.0`，校验值已并入下表。

2026-11-09 图标统一补齐：复选、浮窗、完成状态、窗口控制与复制操作的新增图标同样取自上述固定官方 npm 版本；浮窗旧名称由 `OverlayIcons.js` 映射到标准 Lucide 名称，运行时仍只读取本地生成库。

| 文件 | SHA-256 |
|------|---------|
| `activity.svg` | `3d42112d2bc54483c1ba4ac89336f0a4a26d7544ed19a3dbbe950fd0b37eb8d2` |
| `arrow-down.svg` | `755304acb9101d16746d2ebecc8cf042fe8862bbd126d027618f813b4e2f4e1c` |
| `arrow-right.svg` | `1869af6bbcfce9193d951d8cd4308673a140624f4d44943d7c80b0abd53f6a3a` |
| `arrow-up-down.svg` | `30018f92cc10b3d0b9e976f4bb6f3d820eac899b5777ed743458d514d3e9579c` |
| `arrow-up.svg` | `87245df478e1673410b4bf87243938e1c377c2f73218240d1cbb0ca03b11ac98` |
| `asterisk.svg` | `142069c62a6d12c87ce2d31b83d79abefb72bee6e27bfc0cee64a6076362ef2f` |
| `bell-off.svg` | `725cef4d5ad6c6fd82ec3156d71e86bfe3ec35376782d4a0921bd7b11cd15288` |
| `bell.svg` | `dbdcb73e110c7925e505b6b7a331f181e4fba0339ae12d3d916cf66079283680` |
| `book-open-text.svg` | `8bc24f524dcb358aa4cfe76211fc6c12c40c2f1519f2a87fd916be116d7bf1f5` |
| `calendar.svg` | `69902bf67882aa41c4e8a16548185713a36afd993e601b0ac6566b0912c91b73` |
| `chart-column.svg` | `b967d3ef74b38d1718f1d34f4c49e6fc6db93175a5b5c4362fd40e66270503f6` |
| `check.svg` | `b4c52eac69018dffa07321c57a0d7857d6e76c9c1ccc851350bbc09e6790e7de` |
| `chevron-down.svg` | `2fe6bf9c85691036fb6463026cd0ceb0df0f4e6d57b90badacf009a4d029c105` |
| `chevron-left.svg` | `96ec0362ebdcfe2a8178676a814d0f8c259fc18bf79ec57e90a9d51a8b5f34fe` |
| `chevron-right.svg` | `a4e2552008bea9a6a5cd9b3a9de8d9db39d9b923ff083ba3077114f08bcf0d26` |
| `chevrons-down-up.svg` | `943460b4949b96c3ca8e2e635b3c2116f211d74e7b01ea687dba970649ef484a` |
| `chevrons-down.svg` | `88bbd765e55877aec3985fb5dfd87b677a0295d255c215e83ba4e1e9cb099288` |
| `chevrons-up-down.svg` | `2af42c95adfd1297adf145933f0cf0954d2696f443e9a5f2d960235f8011cd64` |
| `circle-alert.svg` | `f6e3270e82d294804356ed5aa8a17948f065ac17365efdc66426efde5d2a8ffa` |
| `circle-help.svg` | `e6113143231f67e8b8aeeb2b0712f470d0361b725d27e6cd3392ef00472011ee` |
| `clipboard-check.svg` | `23b7297983493c8a6c8e8a125d153d6530ca65ba49f5553a126cd7fa90b5e74e` |
| `clipboard-copy.svg` | `15cc9357ae313a4d32206887282de3125addeab01673361d0e8cb747e0a3ca1a` |
| `clipboard-paste.svg` | `3354ddd4382a6bfe2c6d7cd0091ef70f2cfb2bf09517437c0ab7597b816b6e8f` |
| `clock.svg` | `8a6694e254942c8f349ad603dd6272698ed87f3efe29c73e204caa8ef4ea1728` |
| `cloud-off.svg` | `b32b6eec0f0ddbe5aa6da470acc6d26e826844f84c154c3fdf516ff50e86d952` |
| `copy.svg` | `7dad075505307e180ca7536d02d849cab403f3bcdffc96ea6970e75f770a31b3` |
| `diamond.svg` | `0b0fa8579e57c2e7f651587ca62f6258b411b8e35502bd58989ddd11ea8b35e5` |
| `external-link.svg` | `7794fb77f11607d0ad0a4e3bea4ecddeda1cc87e0e9e42b12157196e0cf8b25c` |
| `file-check.svg` | `f5b97ec5277265788de350034caeee26bc4723228ce1d2138f9b2a00eb381ef9` |
| `file-down.svg` | `2ac3838ae2a627b780a7f7d0913cf6b547d460ae6b506b67c03d51f1dd653692` |
| `file-input.svg` | `5846bf615d309ed4ace903fd710d05166a2887729422aecb9da859fc48dee6a9` |
| `file-json.svg` | `b7bf9105bd3fccc3b35873b03183bec0fd0e872939615a1b9b8b281025f7bc4c` |
| `filter-x.svg` | `6950cc532249ab0dd9887553de1ba85ab6d2d03ee1764f558859c25008597e29` |
| `flag.svg` | `37647b65bba3d175c742673c928acd607a11c2f2f141f93e607990ba2318f012` |
| `folder-open.svg` | `6a9e312a3418ff87f1217766eba6556cbc954772589514100d30ddb830b224b0` |
| `frown.svg` | `f79663d7779e4edf1cf534fec9217886d021899745c77fc7e88304020f89229c` |
| `gamepad-2.svg` | `18a53c618d78e5ab27066f8f0216140d53457cc87cdf928a966ca0bbdb62b7d6` |
| `globe.svg` | `7f8b42da752bf0525462bdac3a48ea51d0298b03077e3ccff1172e0795831835` |
| `grip-vertical.svg` | `e19e472d1f324010885dbd282fda27119d95cbdac408e8697ff1bb0e36dfc59c` |
| `hard-drive.svg` | `4b595eedb88ee0c677f570e8c8b3c56dcfcf8145898c949a73c4a6decdac38f8` |
| `history.svg` | `f869160b6f74d0812033da62b97c1a96801173cf380b3ca56d6092a224f3ad26` |
| `hourglass.svg` | `4ca74ca540649b7638b7bc08183f2da9d6e5f96469dabb0eb003a14ffa1c2df5` |
| `image-down.svg` | `100899ca6a482e377681072fcca778eaf0f86c700f3eb9fe8f7c6fe069c3f777` |
| `image.svg` | `a76d752a3bf99c20bab65453260308799cd90faf32f35c170b19222ace13885e` |
| `inbox.svg` | `e567a335888a078a37e76740e654b582760b7f3f87e5cd4a14a7ca4cb41f8f47` |
| `info.svg` | `e718a8c91a197551cf9c06c06e956f49e9f6e1f967414e18bad44f8591bcadb6` |
| `languages.svg` | `d07e21dd6aaff5c8c9ded188afc09cc8ccb7a0041f76b1d07297fb845d5e2987` |
| `layers.svg` | `e5f5dae86694bcca585995abbfafdfe780648efe891943f69a800882a927990d` |
| `layout-dashboard.svg` | `fc185932ada42aabb7de6c1ccdb91ea3f440aacee14a0461aaacb8b82bcc1541` |
| `lock-open.svg` | `c0a4cd366aa5dcbf95434e071a944fc02065c7834dd995e457e0a9d62f37f3e9` |
| `lock.svg` | `bb33207071e22c9d18ccb7ca1ae0604902476c5d117499a72c776589f039c3a3` |
| `log-out.svg` | `0b7ba4e6c02e640247edd894e97f6a47c4a12b6344611b572e7a0baaf4a88a2c` |
| `maximize-2.svg` | `564a309522844ab5e3f58f8c40e5b52f6e7a02ac86d419c93d6b43e5332115fb` |
| `medal.svg` | `3b4f1b8aaf9a4e98929d5baa3d2b05ca75f7a106567bcf9554a8e86863ac8f1b` |
| `meh.svg` | `098486f09c9b397bdafc8ac5a5238b6f0ac9d79647bc8e95d6306502626d418f` |
| `minimize-2.svg` | `ce27b6463d8f7f927ea4998c70993195645b3115c3d91976dcec2b9b167744eb` |
| `minus.svg` | `5abbf9143d9e133aa7086804083852834201b402290015d9bfbb9f05ffc7a0db` |
| `moon.svg` | `856a8d9cd698f88596d683aa145b5520f9a6f07c03a4a959ec43c794c7ca2d57` |
| `move-diagonal-2.svg` | `f9290af5898ee8eae43ae5ccb2fe39afc9f3338278762fed5972ea27540e0bb7` |
| `network.svg` | `19ac03096a52b7792ffe22c835d0182935b2749af156b3202ab587e4979ada10` |
| `notebook-pen.svg` | `9565cd625066a48517e84f8c83b208fbc96962d8a5e615fdac70b009a896ea4a` |
| `panel-top-close.svg` | `44b3d83bab6444241e3216e2f4b18ced32e08bb2493c3691ebad610bee75e0f5` |
| `plus.svg` | `6ae825b04b65ebd4593252d8a86b031a3dceb68bfde837e0f6611bc2b91e4058` |
| `radio.svg` | `d719fbace88d9c180b9f0d17a8c47f242d9f09d64c29a9e938e3a3a123bb829e` |
| `refresh-cw.svg` | `738d9bb85e8634ff3c29cafe4c71703980af6bc1a4b656f6ff67a77c8a5f7192` |
| `rows-3.svg` | `fe2caa83e57f380f6d5d2d1624b779050243e1338667ada4fa322ea3e7f1ddd2` |
| `search.svg` | `d2c492f6c3e5bfe43d0ec7f22031d10dee141531907ef923fd63f89f3f631a05` |
| `send.svg` | `11d3a7e58866ff244fa6b88a249848c52916801b42aae2bc16d739d2bceb4acc` |
| `server.svg` | `63254fe737b5d1c505261ce56e8ef134a1c9073c0b9b9349efe8181efaa1ed8b` |
| `settings.svg` | `bed6ed745f25d997a1141a057f16e7b89ee2d88ad2c42f9af0819c2d02e6c5f4` |
| `shield-check.svg` | `42f5672d065207deb46eaae95aa87ada4c4a2f3abd1d0d823a9e4bf992177829` |
| `skull.svg` | `3f6c2d27e53b70818276cb179b0541d5d412d4db16ad2070bf1309fef9b7bba8` |
| `sliders-horizontal.svg` | `227f58fd45bc90e5fd9a8a4c8b14fad14089e48f966895b8356efe43bcfed46a` |
| `smile.svg` | `6d91891cf38d3d9cae4fea3dc08cf6e3e1fd00ead52e8c6c3ec57bedd2fcbc06` |
| `square.svg` | `c59b10bcff68f8c815f3879917fb2ea8917553759436fe6c06921112209b961f` |
| `star.svg` | `b3b5608b4eeba47d8e7891abee0e9ad0a8df0646c6959b41ba3ec70c6212292f` |
| `sun.svg` | `e2877a548779ff45be8d74063b88c88119f0ea69c19fd6357baf44ccb9ee2509` |
| `swords.svg` | `168de43f6e4b633492dcf9700a065382897191a4432764e021fb03990efd5fb1` |
| `target.svg` | `bceb97844e2ba7875fbeef2182b6a83d4809ebe5091d472b4fd2a5c6952cc504` |
| `timer.svg` | `5b4c50d0cfa744593819607b8153538c2e052b9e082f145c940d1161c8248b45` |
| `users.svg` | `0bb5660bd932f758647166cda095445e457c64499213cbae514abaeb2e739b00` |
| `x.svg` | `7bfd0cf7c80be3f053277f0ba94946d9cef28dc4d4bda2f1b13b134bcda6c0b6` |
