# MentorRecorder.Desktop

C++20 / Qt 6 Quick / QML / Qt Quick Controls / Qt Graphs 桌面端。

> ## Phase 1 状态
>
> 本目录包含可构建的 Qt 6 CMake 目标、Named Pipe 客户端边界、模拟后端与六页 QML 界面。
> 交互运行默认连接真实 Collector，`--screenshot` 与单元测试仍默认使用模拟数据。
> 使用仓库的 `scripts/build.ps1` 构建时，Collector 运行文件部署至 Desktop 可执行文件的同级目录。
> C++ 单元测试与 QML 离屏加载测试位于 `tests/Desktop.Tests/`。

## 目录用途

| 目录 | 内容 |
|---|---|
| `cpp/` | `main.cpp` 以及暴露给 QML 的 IPC 客户端、模型、格式化器与进程监控 |
| `qml/pages/` | 页面：仪表盘、历史、统计、诊断、设置 |
| `qml/components/` | 可复用组件 |
| `qml/dialogs/` | 对话框：手工补录、更正、软删除/恢复、导出、备份 |
| `qml/charts/` | 基于 Qt Graphs 的图表封装 |
| `resources/` | 图标、字体、qrc、翻译文件 |

## 硬约束

1. **桌面端从不直接访问 SQLite。** 所有数据都来自命名管道
   （契约见 [`../../contracts/ipc-v1.schema.json`](../../contracts/ipc-v1.schema.json)）。
2. **桌面端只作客户端，从不监听。** 不得使用 `QTcpServer` / `QLocalServer` /
   `QWebSocketServer` / `QHttpServer`；不得使用 `QNetworkAccessManager`。
   由 `tools/static-boundary-check` 强制（规则 `NET-005`、`NET-006`）。
3. 界面默认使用简体中文；所有面向用户的字符串均通过 `qsTr()` 处理并纳入翻译文件。
4. 未定义的统计值（`null`）显示为 `—`，**不得以 0 代替**。
5. `DISCONNECTED` / `INTERRUPTED` / `UNKNOWN` 在任何图表中都必须与
   `LEFT_OR_ABANDONED` 分开，**不得合并**
   （见 [`../../docs/statistics-definitions.md`](../../docs/statistics-definitions.md) §7）。
6. 诊断页必须展示 `monitor_type` 与 `injected_hook_enabled`，供用户自行核对边界。

## UI 原型

原始视觉原型仅保留在本地设计资料中，不随公开源码仓库分发。
