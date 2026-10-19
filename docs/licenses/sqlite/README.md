# SQLite 原生库安全升级来源

核对日期：2026-10-19。目标：`net8.0-windows7.0/win-x64`。

Collector 保留 Microsoft.Data.Sqlite 8.0.11 的现有 API，直接固定
`SQLitePCLRaw.bundle_e_sqlite3 [2.1.14]`，使 bundle、core、provider 和 native lib
四个包统一解析为 2.1.14，替换默认的 2.1.6。

本轮全新发布的 `e_sqlite3.dll` 实际返回 SQLite 3.54.0。CVE-2025-6965 影响
3.50.2 之前的 SQLite；新增测试通过实际 SQL 查询核对原生版本，避免旧 DLL 混入
输出后仅凭托管包版本误判。完整后端测试覆盖既有迁移、事务、导入导出和命名管道链路。

`dependency-manifest.json` 记录四个 NuGet 包的 SHA256/内容 SHA512，以及实际发布
DLL 的 SHA256。原始包元数据以 `.nuspec.txt` 存档，不参与还原。包内声明为
Apache-2.0，全文来自 Apache 官方并保存在 `Apache-2.0.txt`；作者、版权和源码仓库
保留在各原始元数据中。上游包没有记录源码提交，清单不推测提交值。

SQLitePCLRaw 封装的许可与 SQLite 本体分开登记：SQLite 本体为 public domain，
依据为 [SQLite 官方版权说明](https://www.sqlite.org/copyright.html)。

本轮仅生成了用于验证的 Collector 发布目录，没有重新打包、安装或发布正式版。
