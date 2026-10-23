# 桌面端测试 / Desktop Tests

本目录是桌面端的 Qt Test 套件（C++20，CMake + Ninja + Qt 6.11.2），随顶层 `CMakeLists.txt` 一起构建，由 `ctest` 运行。测试范围见 [`../../docs/build-and-package.md`](../../docs/build-and-package.md) §3.2。

## 内容

- 纯逻辑：IPC 分帧编解码（与 Collector 侧用同一批样本对拍，见 [`../Fixtures/README.md`](../Fixtures/README.md)）、格式化、分页模型、职业统计、表单校验。
- 控制器：`AppController` 及其拆出的各控制器，使用 `MockBackend` 或真实 IPC 后端。
- 界面：从源码目录加载 QML 组件、对话框与页面，离屏渲染后按对象名核对文案与状态。
- 进程：`CollectorProcess` 的重启退避、租约复用与停止路径；`MentorRecorderIpcIntegration` 与 `MentorRecorderLifecycle` 会拉起真实的 Collector 子进程（临时数据库）。

## 目录

每个套件 `<名称>Tests.cpp` 放在它所测试的桌面端职责目录中，CTest 名称和构建产物位置与目录无关：

| 目录 | 内容 |
|---|---|
| `transport/` | IPC 请求样本、真实 Collector 集成、子进程监护、`MockBackend`、单实例 |
| `workflows/` | 控制器与业务流程：自动记录、完成后协调、恢复、表单校验、历史列表模型 |
| `shell/` | 主窗口、对话框、历史页与详情面板、动效、QML 运行时与告警计数 |
| `statistics/` | 统计浮窗、成就进度、副本构成 |
| `calibration/` | 校准、共享校准、捕获诊断页、候选核对与证据提交 |
| `import/` | 记录导入、截图识别、离线 OCR、职业图标 |
| `reflection/` | 导随心得、心得库与分享图 |
| `components/` | 通用 QML 控件：图标、日期与时间输入、文本框、下拉框、开关 |
| `settings/` | 设置隔离、界面语言、游戏名称、Collector 文本、语音 |
| `update-install/` | 检查更新、下载安装、安装包持有、本次更新 |
| `support/` | 多个套件共用的头文件、`expect_exit.cmake` 与 QML 运行时注册 |

新增套件时把文件放进对应目录，再在 `CMakeLists.txt` 中登记。

## 运行

```powershell
pwsh -File scripts/test.ps1              # 完整构建后运行全部 .NET 与 Qt 测试
ctest --test-dir build --output-on-failure   # 仅运行本目录的套件
```

运行 `ctest` 前须把 Qt 与 MinGW 的 `bin` 目录加入 `PATH`。CTest 为每个测试设置一个不存在的 `MR_COLLECTOR_PATH` 和构建目录内的 `MR_DATA_DIR`；多数套件还在 `main()` 开头调用 `support/TestCollectorGuard.h` 的守卫，单独运行时同样生效。测试只使用隔离的数据目录与管道名，不会触碰用户自己的 Collector、数据库或服务租约。

## 约定

- 新增行为须补充测试；界面文案改动须同步更新按文案断言的用例。
- 复合断言应拆成单独的 `QVERIFY`，空指针检查在解引用前显式返回（`QFAIL`），以便静态分析器能跟踪。
