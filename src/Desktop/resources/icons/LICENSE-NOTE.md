# 图标资源说明 / Icon assets

`jobs/`、`jobs_framed/`、`content_types/`、`roles/` 下的 PNG 是从 FINAL FANTASY XIV 客户端提取的
图标，**版权归 SQUARE ENIX CO., LTD.**，不适用本项目的 GPL-3.0 许可证。随本项目分发的依据如下：

- **FINAL FANTASY XIV Materials Usage License**
  <https://support.na.square-enix.com/rule.php?id=5382&tag=authc>（本次查阅版本 2026-05-07 生效）：
  允许非商业用途，**禁止任何销售或商业用途**，须保留版权与商标声明。

因此本项目及其发行版**不得用于任何商业目的**。版权声明显示在软件的“设置 → 版权与来源”，
也写在 `THIRD_PARTY_NOTICES.md`。这是维护者于 2026-09-07 做出的分发决定，风险由维护者承担。

```
© SQUARE ENIX CO., LTD. All rights reserved.
FINAL FANTASY is a registered trademark of Square Enix Holdings Co., Ltd.
```

## `lucide/` 子目录不是游戏素材

`lucide/` 下的 SVG 是按钮使用的线条图标，来自 Lucide，许可证为 ISC。其中由 Feather 派生的若干图标同时受 MIT 许可证约束，
两段许可证均见 `lucide/LICENSE`，说明见 `lucide/README.md`。这些文件可随本软件分发，与本文件其余部分所述的 SQUARE ENIX 素材无关。

## 来源

- 职业与副本类型图标：XIVAPI v2（`manifest.json`、`content_types.json` 记录每个文件的游戏内路径与 sha256）。
- 职能图标：Gamer Escape 的 Dictionary of Icons（`roles/manifest.json`），`allrounder.png` 由本项目合成。
  XIVAPI 与 Gamer Escape 仅为获取途径，不拥有这些素材的版权。

## 不含图标的构建与自备图标

- CMake 配置时加入 `-DMR_BUNDLE_GAME_ICONS=OFF`，PNG 不编入资源，界面改用文字徽章。
- 本软件优先读取 `%LOCALAPPDATA%\MentorRecorder\icons\` 下与本目录结构相同的 PNG：

```
jobs\<abbr>.png            与 manifest.json 中 icon.file 的相对路径一致
jobs_framed\<abbr>.png
content_types\icon_<id>.png
roles\{tank,healer,melee,ranged,magic,allrounder}.png
```

## 小型职业图标分类器

本项目还从上述本地图标训练小型职业分类器。维护者于 2026-10-10 明确要求将已验证的
固定权重纳入 `1.7.0-beta.3`；本次沿用 2026-09-07 的非商业素材分发决定及版权声明，
不声称取得新的上游许可或独立模型许可。权重 SHA256、训练来源与本次授权范围见
[`../models/job-icon-classifier.metadata.json`](../models/job-icon-classifier.metadata.json)。

`MR_BUNDLE_GAME_ICONS=OFF` 同时排除内嵌分类器权重。训练脚本产生的新权重仍默认
`PUBLIC_DISTRIBUTION_READY=false`，须另行核验来源与分发决定；当前固定模型的授权不自动覆盖重训产物。
