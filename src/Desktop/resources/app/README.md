# 应用图标 / Application icon

`app.png` 与 `app.ico` 是本项目的**原创**图标（深色圆角底、金色罗盘环），由
仓库脚本通过 Pillow 程序化绘制，不含任何游戏素材，随本项目按 GPL-3.0-or-later 分发。

- `app.rc.in` 将图标与版本资源编入 `MentorRecorder.Desktop.exe`。该文件为模板，
  CMake 通过 `configure_file` 将其连同 `app.ico` 一并生成至 `build/src/Desktop/`，版本号取自
  `Directory.Build.props`（见 [docs/build-and-package.md](../../../../docs/build-and-package.md)）；
- `src/Collector/MentorRecorder.Collector.csproj` 的 `ApplicationIcon` 指向同一文件；
- `installer/MentorRecorder.iss` 以其作为安装器图标。
