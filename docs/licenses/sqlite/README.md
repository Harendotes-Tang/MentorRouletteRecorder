# SQLite 依赖来源与原生资产核对

核对日期：2026-11-03。目标：`net8.0-windows7.0/win-x64`。

Collector 使用 Microsoft.Data.Sqlite 10.0.14，并直接固定
`SQLitePCLRaw.bundle_e_sqlite3 [3.0.5]`。还原后的依赖图包含
Microsoft.Data.Sqlite/.Core 10.0.14、SQLitePCLRaw 的 bundle、config、core、provider
四个 3.0.5 包，以及原生包 `SQLite 3.53.4`。bundle 是元包，
`SQLitePCLRaw.batteries_v2.dll` 由 config 包提供，`e_sqlite3.dll` 由 SQLite 包提供。

本次直接加载 NuGet 缓存中 win-x64 的 `e_sqlite3.dll`，
`sqlite3_libversion()` 返回 3.53.4，`sqlite3_sourceid()` 返回：

```text
2026-07-24 19:02:57 bf7c7f30031888f4e796e429ab3978879485813aaca6f641c7b33e4e09459bcc
```

`dependency-manifest.json` 记录七个 NuGet 包的 SHA256/内容 SHA512，以及当前还原图
选中的五个运行 DLL 的 SHA256 和包内路径。原始包元数据以 `.nuspec.txt` 存档，
不参与还原。Microsoft.Data.Sqlite 系列声明 MIT，正文保存在 `MIT.txt`；
SQLitePCLRaw 系列声明 Apache-2.0，正文保存在 `Apache-2.0.txt`。
作者、版权和上游记录的源码提交保留在原始元数据及清单中；上游未提供提交时保持空值。

原生 SQLite 包的 `LICENSE.txt` 原文另存为 `SQLite-Public-Domain.txt`，
其 public domain 声明来源为 [SQLite 官方版权说明](https://sqlite.org/copyright.html)。

以上哈希与版本来自已还原的官方包缓存；构建输出、发布目录和安装包需要由对应流程
核对。本次清单核对未执行安装或对外发布。
