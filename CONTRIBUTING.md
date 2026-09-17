# 贡献指南 / Contributing

感谢您对导随记录器的关注。本文说明报告问题、提出建议和提交代码的流程与要求。

本项目对隐私与网络行为有严格限制。提交任何改动前，请先阅读[项目边界](#项目边界)一节。

## 目录

- [项目边界](#项目边界)
- [贡献方式](#贡献方式)
  - [报告缺陷](#报告缺陷)
  - [提出功能建议](#提出功能建议)
  - [报告安全漏洞](#报告安全漏洞)
  - [提供真机证据](#提供真机证据)
  - [分享协议校准](#分享协议校准)
- [开发环境](#开发环境)
- [开发规范](#开发规范)
  - [代码风格](#代码风格)
  - [界面文案](#界面文案)
  - [协议档案](#协议档案)
  - [测试](#测试)
  - [变更记录](#变更记录)
  - [提交信息](#提交信息)
- [提交 Pull Request](#提交-pull-request)
- [许可](#许可)

## 项目边界

以下限制是本项目的基本前提，违反其中任何一项的 Pull Request 不予合并。

- 不注入进程，不读取游戏进程内存，不安装钩子，不发送数据包，不提供自动化或反检测功能。
- 不监听网络端口，不包含遥测、云端服务或自动下载安装的更新机制。
- 出站请求仅限以下三类，均由采集服务发出。桌面端不发起任何网络请求。不接受新增其他网络访问。
  - 共享校准的只读下载（[docs/privacy-boundary.md](docs/privacy-boundary.md) §8.2，默认开启，可关闭）；
  - 用户自行启用并填写密钥后的在线语音合成（同文档 §8.3，默认关闭）；
  - 只读取发布页版本号、仅用于提示的更新检查（同文档 §8.4，默认开启，可关闭）。
- 不长期保存原始报文。日志与诊断报告不得包含负载、IP 地址、文件路径或账号信息。
- 不内置或分发 Npcap、`oo2net_9_win64.dll`、`deucalion-*.dll` 或任何游戏客户端文件。
  随包分发的游戏图标依据 FINAL FANTASY XIV Materials Usage License 使用，范围与条件见
  [docs/third-party-licenses.md](docs/third-party-licenses.md)。
- 不从 AGPL 许可的项目复制代码或常量表，不通过逆向游戏客户端获取内部结构。

上述规则由 `tools/static-boundary-check` 静态检查强制执行，`scripts/verify.ps1` 会运行该检查。
完整定义见 [docs/privacy-boundary.md](docs/privacy-boundary.md)。

## 贡献方式

### 报告缺陷

提交前，请先在 [Issues](https://github.com/Harendotes-Tang/MentorRouletteRecorder/issues) 中搜索，确认问题未被报告过。

新建 issue 时请使用「问题反馈 / Bug report」模板，并提供：

- 软件版本（"设置 → 关于"，或 `MentorRecorder.Collector.exe --version` 的输出）；
- 区服与客户端版本；
- 实际行为、期望行为与复现步骤；
- 如有必要，附上"捕获诊断 → 导出脱敏诊断报告"生成的 JSON。

**请勿**在公开 issue 中粘贴原始报文、角色名或其他玩家的信息。

### 提出功能建议

请使用「功能建议 / Feature request」模板，说明要解决的问题和建议的方案，
并确认该建议不需要突破[项目边界](#项目边界)。

### 报告安全漏洞

**请勿**通过公开 issue 报告安全问题。请按 [SECURITY.md](SECURITY.md) 的说明，
通过 GitHub Security Advisories 私下报告。

### 提供真机证据

协议相关的问题通常需要真机流量作为证据。请使用候选验证页面的「导出证据」功能生成 JSON
（同目录附有 SHA-256 校验文件），并通过私密渠道提交给维护者，**不要**附在公开 issue 或 PR 中。
操作步骤见 [docs/live-validation-guide.md](docs/live-validation-guide.md) §7.1。

### 分享协议校准

游戏版本更新后，校准结果在软件内直接分享，发布到公开数据仓库
[MentorRecorder-Calibrations](https://github.com/Harendotes-Tang/MentorRecorder-Calibrations)，
不经过本仓库的 Pull Request。

## 开发环境

所需工具：

| 工具 | 版本 |
|---|---|
| .NET SDK | 8.x |
| Qt | 6.11.2（MinGW 13） |
| CMake | ≥ 3.24 |
| Ninja | — |
| PowerShell | 7 |
| Python | 3.x（运行 `tools/` 下的自测；签名工具的测试另需 `capstone` 与 `pefile`，版本见 CI 配置） |

工具链路径通过环境变量 `MR_QT_PREFIX`、`MR_MINGW_BIN`、`MR_NINJA_EXE`、`MR_CMAKE_EXE` 指定，
详见 [docs/build-and-package.md](docs/build-and-package.md)。

```powershell
pwsh -File scripts/bootstrap.ps1   # 检查工具链
pwsh -File scripts/build.ps1       # 构建
pwsh -File scripts/test.ps1        # 运行 .NET 与 Qt 测试
pwsh -File scripts/verify.ps1      # 边界检查与全部测试（提交前必须通过）
pwsh -File scripts/static-analysis.ps1   # 桌面端 C++ 的 clang-tidy 与 cppcheck
```

## 开发规范

### 代码风格

- 遵循仓库根目录的 [.editorconfig](.editorconfig)（UTF-8、CRLF、4 空格缩进；JSON / YAML / Markdown 为 2 空格）。
- C#：已启用 `Nullable` 与 `TreatWarningsAsErrors`，构建必须零警告；引入新依赖前请确认不产生警告。
- C++：C++20；QML 使用 Qt 6 Quick。
- Markdown：须通过 markdownlint 检查，规则见 [.markdownlint.json](.markdownlint.json)。

### 界面文案

- 面向玩家的文案使用中文。
- 不向玩家显示十六进制数值或内部术语。
- QML 冒烟测试（`ctest`）以离屏方式加载界面并截图，部分用例通过 `--verify-text` 核对关键文案。修改相关文案时请同步更新测试。

### 协议档案

`protocol-profiles/` 中的每个 opcode、偏移与编号，都必须在 `provenance.evidence` 中注明来源，来源限于以下三类：

- 本机流量观察（注明样本数）；
- 公开且许可证兼容的文档（注明 URL 与访问日期）；
- 用户按[真机验证流程](docs/live-validation-guide.md)完成的核对。

通过试错或推测得到的常量不予接受。修改档案后，请运行以下命令重新签章：

```powershell
python tools/protocol-profile-validator/validate.py --stamp <file>
```

### 测试

- 修改行为时须补充或更新测试。
- 修改 IPC 契约时须同步更新 `contracts/` 及 [contracts/CHANGELOG.md](contracts/CHANGELOG.md)。
- `tools/` 下的 Python 工具由 `scripts/run-python-tool-tests.ps1` 运行自测（`selftest.py` 与 `test_*.py`），`verify.ps1` 会调用该脚本。

### 变更记录

用户可见的改动请记入 [CHANGELOG.md](CHANGELOG.md) 的 `[Unreleased]` 段落，格式遵循
[Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)。已发布版本的段落不得修改，打包脚本会校验这一点。

### 提交信息

遵循 [Conventional Commits](https://www.conventionalcommits.org/zh-hans/v1.0.0/) 规范：

```
<type>(<scope>): <subject>
```

常用类型：`feat`、`fix`、`docs`、`test`、`refactor`、`perf`、`ci`、`chore`。
常用范围：`collector`、`desktop`、`capture`、`calibration`、`diagnostics`、`ci`、`release`。

## 提交 Pull Request

1. Fork 本仓库，并基于 `main` 创建分支。
2. 完成修改，确保 `pwsh -File scripts/verify.ps1` 在本地通过。
3. 按上文规范更新测试、文档与变更记录。
4. 向 `main` 发起 Pull Request，在描述中说明改动内容、原因及验证方式；如关联 issue，请注明编号。
5. CI 通过后等待维护者审查。

每个 Pull Request 应只做一件事，无关的改动请分别提交。

## 许可

本项目以 [GPL-3.0-or-later](LICENSE) 发布。提交贡献即表示您同意以相同许可授权您的贡献。
