# 截图 / Screenshots

本目录存放 [README.md](../../README.md) 引用的界面截图。截图由桌面端的无头截图模式以模拟数据生成，
标题栏带有“模拟数据”标识。界面或版本号发生变化后，需按下列命令重新生成：

```powershell
$exe = 'build\src\Desktop\MentorRecorder.Desktop.exe'
& $exe --screenshot docs\screenshots\dashboard-light.png --page 1 --theme light `
    --mock-recording-state listening --mock-live entered --screenshot-size 1440x900 --screenshot-delay 1500
& $exe --screenshot docs\screenshots\history-light.png --page 2 --theme light `
    --mock-recording-state listening --mock-live entered --screenshot-size 1440x900 --screenshot-delay 1500
```

运行前需将 Qt 与 MinGW 的 `bin` 目录加入 `PATH`，配置方式见
[build-and-package.md](../build-and-package.md)。参数 `--mock-recording-state listening`
使模拟后端处于正常监听状态；缺少该参数时，截图中会出现“协议档案不匹配”的演示弹窗。
