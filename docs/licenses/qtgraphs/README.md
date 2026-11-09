# 项目专用 QtGraphs 6.11.2

QtGraphs 的 `CommonUtils::maxTextureSize()` 在 Windows 上固定创建 D3D12 设备。当前 MinGW Qt SDK 没有编译 D3D12 支持，导致每个进程首次创建图表主题时报警。本补丁只把这次纹理尺寸探测改为 D3D11；不改变窗口图形后端、不关闭图表功能，也不屏蔽日志。

上游来源、固定提交、源码压缩包 SHA256、补丁及修补前后文件 SHA256 见 `dependency-manifest.json`。`qtgraphs-d3d11.patch` 是完整修改。上游代码的 GPL-3.0-only 许可证正文见 `GPL-3.0-only.txt`。

开发构建由 CMake 自动调用 `scripts/bootstrap-qtgraphs.ps1`；Qt Creator 直接构建 Desktop 或测试目标也会执行。缓存默认位于 `%LOCALAPPDATA%/MentorRecorder-build-dependencies/qtgraphs`，可用 `MR_QT_GRAPHS_CACHE_DIR` 指定其他不超过 100 字符的 ASCII 路径。固定源码包随项目保存，首次构建离线解压并编译；之后核对 SDK、编译器、配方和 DLL 哈希后复用。`MR_QT_GRAPHS_SOURCE_ARCHIVE` 可指定另一份相同 SHA256 的本地源码包。准备和部署脚本不访问网络。系统 Qt SDK 始终只读。

准备脚本使用当前 CMake 构建传入的 Qt 前缀、MinGW 编译器、CMake 和 Ninja。默认 Release 依赖与此 SDK 的 release-only Qt6Graphs.dll 对应，Desktop 的 Debug/Release 均复用它。构建必须保留 SDK 全部 12 项 2D/3D 功能，并包含原 DLL 的全部导出名，否则拒绝部署。

发布时在 `windeployqt` 之后重新部署并核验本库；`runtime.json` 记录实际 DLL、SDK、源码和补丁哈希。发布包此目录还包含 `qtgraphs-source.tar.gz`，它与补丁一起构成本次修改库的对应源码。重建时从该原始包解压、应用此补丁，并使用同版本 Qt 6.11.2 MinGW x64 SDK 运行项目准备脚本。源码下载地址与哈希均在清单中，不依赖可变的分支名。
