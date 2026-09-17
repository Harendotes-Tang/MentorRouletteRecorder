# 内置字体

| 文件 | 家族 | 来源 | 许可 |
|------|------|------|------|
| `Cinzel-Regular.ttf` | Cinzel | <https://github.com/NDISCOVER/Cinzel> `fonts/ttf/`（即 google/fonts `ofl/cinzel/` 的上游项目） | SIL Open Font License 1.1（见 `OFL.txt`） |
| `Cinzel-Bold.ttf` | Cinzel | 同上 | 同上 |
| `IBMPlexMono-Medium.ttf` | IBM Plex Mono | <https://github.com/IBM/plex> 发布 `@ibm/plex-mono@2.5.0` 的 `ibm-plex-mono.zip` → `fonts/complete/ttf/`（字体版本 2.005） | SIL Open Font License 1.1（见 `IBMPlexMono-OFL.txt`，即压缩包里的 `LICENSE.txt` 原文） |
| `IBMPlexMono-SemiBold.ttf` | IBM Plex Mono | 同上 | 同上 |

Cinzel 是艾欧泽亚（eorzea）界面风格中标题与数字使用的衬线字体
（`Theme.headingFamily`），仅包含拉丁字形。中文标题由
`Theme.headingFamilyCjk = "Noto Serif SC"` 承担；`main.cpp` 通过
`QFont::insertSubstitutions()` 为其登记了 `SimSun` / `NSimSun` /
`Songti SC` / `Noto Serif CJK SC` 作为回退。

google/fonts 目前仅提供可变字体 `Cinzel[wght].ttf`。为使 Qt 在离屏
（freetype）后端上稳定获得真正的 Bold 字形，本目录改用上游项目的两个静态字重。

IBM Plex Mono 是经典（workbench）界面风格中数字使用的等宽字体
（`Theme.numFamily` / `Theme.figureFamily`，字重 600；导航序号与等宽路径使用 500）。
仅取 Medium / SemiBold 两个静态字重，原样未改。"Plex" 为保留字体名，修改后的版本不得沿用。

| 文件 | SHA-256 |
|------|---------|
| `IBMPlexMono-Medium.ttf` | `98fbd727aae340b236955879dabed4d991aac9e8e90b3b2a67ce4a59221cc97c` |
| `IBMPlexMono-SemiBold.ttf` | `f04d7c488ddf7d1fa99f2574efc3406ea4cbe17bb1af3a1ab960f84d0c96a172` |

OFL 允许再分发，前提是保留许可证全文（Cinzel 为 `OFL.txt`，IBM Plex Mono 为
`IBMPlexMono-OFL.txt`）。与 `resources/icons/` 下的 SQUARE ENIX 素材不同，
这四个字体文件**可以**随本软件公开分发。
