# 桌面端测试 / Desktop Tests

本目录是桌面端的 Qt Test 套件（C++20，CMake + Ninja + Qt 6.11.2），随顶层 `CMakeLists.txt` 一起构建，由 `ctest` 运行。测试范围见 [`../../docs/build-and-package.md`](../../docs/build-and-package.md) §3.2。

## 内容

- 纯逻辑：IPC 分帧编解码（与 Collector 侧用同一批样本对拍，见 [`../Fixtures/README.md`](../Fixtures/README.md)）、格式化、分页模型、职业统计、表单校验。
- 控制器：`AppController` 及其拆出的各控制器，使用 `MockBackend` 或真实 IPC 后端。
- 界面：从源码目录加载 QML 组件、对话框与页面，离屏渲染后按对象名核对文案与状态。
- 进程：`CollectorProcess` 的重启退避、租约复用与停止路径；`MentorRecorderIpcIntegration` 与 `MentorRecorderLifecycle` 会拉起真实的 Collector 子进程（临时数据库）。

## 运行

```powershell
pwsh -File scripts/test.ps1              # 完整构建后运行全部 .NET 与 Qt 测试
ctest --test-dir build --output-on-failure   # 仅运行本目录的套件
```

运行 `ctest` 前须把 Qt 与 MinGW 的 `bin` 目录加入 `PATH`。每个测试二进制在 `main()` 开头调用 `TestCollectorGuard.h` 的守卫：测试只使用隔离的数据目录与管道名，不会触碰用户自己的 Collector、数据库或服务租约。

## 约定

- 新增行为须补充测试；界面文案改动须同步更新按文案断言的用例。
- 复合断言应拆成单独的 `QVERIFY`，空指针检查在解引用前显式返回（`QFAIL`），以便静态分析器能跟踪。
