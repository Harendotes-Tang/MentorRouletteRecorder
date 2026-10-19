# XLS 导入依赖核对

首次核对日期：2026-10-10；安全依赖更新：2026-10-19。
实际框架：`net8.0-windows7.0/win-x64`。

Collector 固定 NPOI `[2.7.6]`，只调用 HSSF 读取真实 BIFF8，并保留已有 XLSX ZIP/XML
解析。NPOI NuGet 包内 `npoi.nuspec` 记录 Apache-2.0 和提交
`a0f50a01a845aa4dd793cee0ac5b776745966cb6`，包内 `LICENSE` 原样保留。
未使用 2.8 系列。每个本次新增包的原始 `.nuspec` 以 `.nuspec.txt` 后缀存档，
字节内容保持不变；许可证、版权和确切源码地址分别保存在本目录及
`dependency-manifest.json`。这些上游元数据副本仅用于许可和来源核对，不参与 NuGet
还原；文本后缀避免 GitHub 将归档副本识别为本项目的 NuGet 依赖清单。实际版本以
Collector 的 `PackageReference`、还原后的 `project.assets.json` 和发布产物为准，
本目录清单登记对应版本及哈希。归档后缀于 2026-10-19 核对调整。
Math.NET 的包未记录提交，其许可证来自
对应 `v5.0.0` 源码标签。NSax 1.0.2 为 LGPL-3.0-only，不是 Apache-2.0，其源码提交为
`b75861cbc49be1ce4b02410e324b5755ffcb17a2`，未修改上游二进制。

`dependency-manifest.json` 有 14 个 XLS 相关还原包，其中 13 个包含实际发布运行
资产，ImageSharp 为编译资产。它同时记录 NuGet 包 SHA256、NuGet 内容 SHA512、
实际发布 DLL 的 SHA256、许可证来源和源码仓库；既有 Machina、Sqlite、SharpPcap
及其传递依赖分别声明；SQLite 安全升级的版本和原生 DLL 哈希见
[`../sqlite/`](../sqlite/)。

## 运行资产与审计

NPOI 默认依赖的 System.Security.Cryptography.Xml 8.0.2 有已知漏洞，明确锁定同一
.NET 8 系列修复版 `[8.0.5]`。其许可证仍为 MIT，实际传递 Pkcs 版本为 8.0.2。

SixLabors.Fonts `[1.0.1]` 保留运行资产：它由 NPOI 公式字符串解析路径使用，回归样本
生成在没有该库的全新目录中曾抛出缺少程序集错误。实际 `.nuspec` 和对应源码的
Apache-2.0 全文都保留。导入不会进行自动列宽测量或图片尺寸计算。

SixLabors.ImageSharp 固定到修复版 `[4.1.3]`，在 Collector 以及两个后端测试项目的
`PackageReference` 上均明确 `IncludeAssets="compile"`，仅取编译引用，不引入图片
运行库或该包的构建工具。项目引用的资产排除不会自动传到测试项目，因此三处保持
同一限制。新构建/发布目录没有
ImageSharp DLL，`.deps.json` 没有 ImageSharp DLL 运行声明，避免仅删除 DLL 导致启动失败。
实际 HSSF 文件、公式缓存拒绝、日期样式和含普通嵌入 PNG 的固定 XLS 均在该配置下
验证；嵌入图片导入测试还检查当前进程没有加载 ImageSharp 程序集。

包内原始 `LICENSE` 采用 Six Labors Split License，其第 2 节对用于开源软件的情形
授予 Apache-2.0。本项目以 GPL-3.0-or-later 开源，按此条款登记适用的 Apache-2.0，
并原样保留该许可全文，见 `SixLabors.ImageSharp-4.1.3-LICENSE.txt`。此结论只描述
本项目的开源使用条件，不代表该包对任意用途均无条件采用 Apache-2.0。
适用的 Apache-2.0 全文见 [`../sqlite/Apache-2.0.txt`](../sqlite/Apache-2.0.txt)。

升级后删除了三个项目中针对下列五项公告的全部 `NuGetAuditSuppress`。
仓库默认启用 `NuGetAuditMode=all`，审计直接依赖、传递依赖和编译资产；2026-10-19
三个项目的在线漏洞清单均为空。ImageSharp 新增的传递依赖 System.IO.Hashing 8.0.0
按 MIT 保留运行资产，其许可证、第三方声明和 DLL 哈希一起登记在本目录。

- `https://github.com/advisories/GHSA-gwg2-r3hj-4w44`
- `https://github.com/advisories/GHSA-j3p4-wp97-rph4`
- `https://github.com/advisories/GHSA-j9gm-c75j-xc9q`
- `https://github.com/advisories/GHSA-jjfr-hcj7-qf5w`
- `https://github.com/advisories/GHSA-wmxv-xphr-5c9g`

## 证据

本次变更的 `openspec/changes/add-import-templates-and-xls/evidence/` 保留目标单测、
真实唯一管道/临时库回归、新发布构建、进程启动/真实文件导入、运行资产清单及哈希。
`backend-npoi-depth-probe.log` 是隔离子进程对上游库的原始 10000 层 OLE 样本核查：
库成功展开全部层级，未发生栈溢出。生产解析器在进入 NPOI 前校验原始 FAT/目录图，
限制 16 层、1024 部件和循环，真实深链回归确认先返回可恢复错误。

打包脚本已有递归收集 `docs/licenses/` 的流程，本目录随该流程收集。此次未创建安装包
或公开发布；本文登记已经核对的本地依赖和验证，不代表外部机器验收。
