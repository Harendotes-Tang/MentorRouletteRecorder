# 安全问题报告 / Security Policy

本软件被动读取本机网卡流量，并加载游戏可执行文件的一份临时副本进行特征扫描。
边界定义见 [docs/privacy-boundary.md](docs/privacy-boundary.md)。若发现本软件越过上述边界，
或发现任何可能泄露玩家数据、可被利用的问题，请**不要**提交公开 issue。

- 通过 GitHub 的 “Report a vulnerability”（Security Advisories）私下报告；
- 说明复现步骤、影响范围与所用版本（“设置 → 关于”或 `MentorRecorder.Collector.exe --version`）；
- 请勿附带原始报文、账号或角色信息。

维护者收到报告后将尽快确认，并在修复发布后公开致谢；不希望致谢的，请在报告中说明。

支持的版本：仅最新的正式版本接收安全修复。
