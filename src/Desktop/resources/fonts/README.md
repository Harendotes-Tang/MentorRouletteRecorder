# 内置字体

| 文件 | 家族 | 来源 | 许可 |
|------|------|------|------|
| `Cinzel-Regular.ttf` | Cinzel | <https://github.com/NDISCOVER/Cinzel> `fonts/ttf/`（即 google/fonts `ofl/cinzel/` 的上游项目） | SIL Open Font License 1.1（见 `OFL.txt`） |
| `Cinzel-Bold.ttf` | Cinzel | 同上 | 同上 |
| `IBMPlexMono-Medium.ttf` | IBM Plex Mono | <https://github.com/IBM/plex> 发布 `@ibm/plex-mono@2.5.0` 的 `ibm-plex-mono.zip` → `fonts/complete/ttf/`（字体版本 2.005） | SIL Open Font License 1.1（见 `IBMPlexMono-OFL.txt`，即压缩包里的 `LICENSE.txt` 原文） |
| `IBMPlexMono-SemiBold.ttf` | IBM Plex Mono | 同上 | 同上 |
| `NotoSerifSC-Regular.otf` | MR Reflection Serif（Noto Serif SC 2.003） | [notofonts/noto-cjk](https://github.com/notofonts/noto-cjk/tree/f8d157532fbfaeda587e826d4cd5b21a49186f7c/Serif/SubsetOTF/SC)，静态 Regular，仅修改内部名称 | SIL OFL 1.1（`NotoSerifSC-OFL.txt`） |
| `NotoSerifSC-Bold.otf` | MR Reflection Serif（Noto Serif SC 2.003） | 同上，静态 Bold，仅修改内部名称 | 同上 |
| `NotoSansSC-Regular.otf` | Noto Sans SC 2.004 | [同一固定提交的 Sans/SubsetOTF/SC](https://github.com/notofonts/noto-cjk/tree/f8d157532fbfaeda587e826d4cd5b21a49186f7c/Sans/SubsetOTF/SC)，静态 Regular | SIL OFL 1.1（`NotoSansSC-OFL.txt`） |

Cinzel 是艾欧泽亚（eorzea）界面风格中标题与数字使用的衬线字体
（`Theme.headingFamily`），仅包含拉丁字形。中文标题由
`Theme.headingFamilyCjk = "MR Reflection Serif"` 承担。
主题标题与分享图均显式使用此内部名称，避免 Windows 与系统同名字体混用轮廓。
Noto Serif SC 的常规与粗体、心得列表的 Noto Sans SC 常规体均随程序内置，
由 `registerBundledFonts()` 在 QML 加载前注册，不安装到系统、不在运行时下载。
系统宋体只保留为字体资源异常或未覆盖字形时的兜底。

三份 Noto 字体取自官方地区版静态 OTF，保留上游全部字形，未按界面文案裁剪。
Serif 两份字体仅用 FontTools 修改 name 表的家族、全名、唯一标识及 PostScript 名
（name ID 1/3/4/6/16/18/21），以及 CFF 的 FontName/FamilyName/FullName；
常规与粗体分别使用 `MRReflectionSerif-Regular`、`MRReflectionSerif-Bold`，
字形轮廓及 Unicode 映射保持不变。Sans 保持上游原文件。
静态字重避免 Qt 将系统变量字体的常规字形合成为粗体。字体内版权声明分别为
`© 2017-2024 Adobe`（Serif）与 `© 2014-2021 Adobe`（Sans）；上游原文许可证
分别来自同一提交的 `Serif/LICENSE` 与 `Sans/LICENSE`。

google/fonts 目前仅提供可变字体 `Cinzel[wght].ttf`。为使 Qt 在离屏
（freetype）后端上稳定获得真正的 Bold 字形，本目录改用上游项目的两个静态字重。

IBM Plex Mono 是经典（workbench）界面风格中数字使用的等宽字体
（`Theme.numFamily` / `Theme.figureFamily`，字重 600；导航序号与等宽路径使用 500）。
仅取 Medium / SemiBold 两个静态字重，原样未改。"Plex" 为保留字体名，修改后的版本不得沿用。

| 文件 | SHA-256 |
|------|---------|
| `IBMPlexMono-Medium.ttf` | `98fbd727aae340b236955879dabed4d991aac9e8e90b3b2a67ce4a59221cc97c` |
| `IBMPlexMono-SemiBold.ttf` | `f04d7c488ddf7d1fa99f2574efc3406ea4cbe17bb1af3a1ab960f84d0c96a172` |
| `NotoSerifSC-Regular.otf` | `0fd7d02a8ceddd94c67c584f8d9e107f0bc18d15b9769c9aa2c4331b8e2715a7` |
| `NotoSerifSC-Bold.otf` | `ac4ad98a4272a59ae8d53e1c1c839e185fa3b62eec475185cdb3232d596b31a0` |
| `NotoSansSC-Regular.otf` | `faa6c9df652116dde789d351359f3d7e5d2285a2b2a1f04a2d7244df706d5ea9` |
| `NotoSerifSC-OFL.txt`、`NotoSansSC-OFL.txt` | `6a73f9541c2de74158c0e7cf6b0a58ef774f5a780bf191f2d7ec9cc53efe2bf2` |

OFL 允许再分发，前提是保留各家族许可证全文。与 `resources/icons/` 下的
SQUARE ENIX 素材不同，本目录的字体文件**可以**随本软件公开分发。
