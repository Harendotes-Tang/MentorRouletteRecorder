# XLS 导入依赖核对

核对日期：2026-10-10。实际框架：`net8.0-windows7.0/win-x64`。

Collector 固定 NPOI `[2.7.6]`，只调用 HSSF 读取真实 BIFF8，并保留已有 XLSX ZIP/XML
解析。NPOI NuGet 包内 `npoi.nuspec` 记录 Apache-2.0 和提交
`a0f50a01a845aa4dd793cee0ac5b776745966cb6`，包内 `LICENSE` 原样保留。
未使用 2.8 系列。每个本次新增包的原始 `.nuspec`、许可证、版权和确切源码地址分别
保存在本目录及 `dependency-manifest.json`；Math.NET 的包未记录提交，其许可证来自
对应 `v5.0.0` 源码标签。NSax 1.0.2 为 LGPL-3.0-only，不是 Apache-2.0，其源码提交为
`b75861cbc49be1ce4b02410e324b5755ffcb17a2`，未修改上游二进制。

`dependency-manifest.json` 有 13 个本次引入的还原包，其中 12 个包含实际发布运行
资产，ImageSharp 为编译资产。它同时记录 NuGet 包 SHA256、NuGet 内容 SHA512、
实际发布 DLL 的 SHA256、许可证来源和源码仓库；既有 Machina、Sqlite、SharpPcap
及其传递依赖保持原声明。

## 运行资产与审计

NPOI 默认依赖的 System.Security.Cryptography.Xml 8.0.2 有已知漏洞，明确锁定同一
.NET 8 系列修复版 `[8.0.4]`。其许可证仍为 MIT，实际传递 Pkcs 版本为 8.0.1。

SixLabors.Fonts `[1.0.1]` 保留运行资产：它由 NPOI 公式字符串解析路径使用，回归样本
生成在没有该库的全新目录中曾抛出缺少程序集错误。实际 `.nuspec` 和对应源码的
Apache-2.0 全文都保留。导入不会进行自动列宽测量或图片尺寸计算。

SixLabors.ImageSharp `[2.1.11]` 为 Apache-2.0 编译资产，在 Collector 以及两个后端
测试项目的 `PackageReference` 上明确 `ExcludeAssets="runtime"`。项目引用的资产
排除不会自动传到测试项目，因此两处测试项目也保持同一排除。新构建/发布目录没有
ImageSharp DLL，`.deps.json` 没有 ImageSharp DLL 运行声明，避免仅删除 DLL 导致启动失败。
实际 HSSF 文件、公式缓存拒绝、日期样式和含普通嵌入 PNG 的固定 XLS 均在该配置下
验证；嵌入图片导入测试还检查当前进程没有加载 ImageSharp 程序集。

NuGet 仍能看到编译包的图片漏洞。仅对下面五个已知 ImageSharp advisory 添加精确
`NuGetAuditSuppress`，理由是该包没有运行资产，导入不调用图片编解码；没有全局关闭
审计，也没有把 NuGet 审计结果当作所有发布依赖均无漏洞的保证。任何新的包/advisory
仍按默认审计处理。现有 SQLitePCLRaw.lib.e_sqlite3 2.1.6 的审计条目属于既有依赖，
本次未改变其版本或抑制规则。

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
