# 安全与质量门禁

## 本地与 CI

完整入口仍是 `scripts/verify.ps1`。它先执行 `scripts/security-quality.ps1 -Phase Local`，
对 solution 的三个 C# 项目执行强制全依赖审计恢复，再核对完整解析图和直接/传递漏洞结果。
缺项目、缺结果、审计不可达、检查期间源码变化都不会通过。随后构建和测试复用该恢复结果；
`package.ps1` 的最终 `win-x64 --self-contained true` publish 另外开启完整审计，覆盖实际分发图。

```powershell
pwsh -NoProfile -File scripts/verify.ps1 -Configuration Release
pwsh -NoProfile -File scripts/package.ps1 -Configuration Release -Verify
```

完整验证还包括静态隐私/架构边界、检查器正反例、实际 TRX/Qt 测试结果、cppcheck、
监听/载荷/许可等已有检查。TRX 缺关键计数、整组跳过、逐项与汇总不一致，以及关键 Qt
集成缺少实际完成日志都按失败处理。测试结果写入独立的 `artifacts/test-results/run-*`。
cppcheck 缺工具、超时、错误 XML 或硬错误也会失败；风格提示仍按已有政策单列。

CI 使用同一入口并上传测试、安全和静态分析证据。独立检查器 job 验证安全门禁的正反例。
常规 CI 筛除 Soak；正式完整验收不传 `-SkipGate`、`-TestFilter` 或 `-NoBuild`。
CI 不生成安装包，也不等于安装或真实抓包验收。

## 推送、合入与发布

```powershell
pwsh -NoProfile -File scripts/security-quality.ps1 -Phase PrePush
pwsh -NoProfile -File scripts/security-quality.ps1 -Phase PostPush
pwsh -NoProfile -File scripts/security-quality.ps1 -Phase Merge
pwsh -NoProfile -File scripts/security-quality.ps1 -Phase Release
```

以上检查只读 GitHub，不修改分支保护、扫描服务、令牌权限、发布或 Git hooks。
仓库策略在 `tools/security-quality/policy.json`：准确提交的必需 CI 和 Dependabot 为必需，
实际已启用的代码/密钥扫描也纳入合入/发布检查。无法查明配置不能当作未启用；
没有分析、权限不足、接口出错及检查未完成均不得标记 PASS。

PrePush 允许为修复已知告警提交改动，开放告警仍明确披露；PostPush 读回准确提交的 CI。
Merge/Release 不允许把旧提交绿灯、本地另一个输入的图、镜像 job 或错误分支的扫描结果拼成通过。
存在未提交修改时，远端 CI 不能证明这些修改已验证，正式放行会阻断。

`-SkipLocal` 仅供远端状态读回；在 Merge/Release 中不构成完整放行，不能把它的状态当作
本地审计通过。新增必需检查尚未推送运行时，也必须保持未通过。

## 证据与权限

每次检查写入独立的 `artifacts/security-quality/<run-id>/report.json`；指定输出目录必须不存在。
报告记录输入摘要、提交、时间、必需性、配置状态和结果，不保存令牌、密钥值或原始密钥扫描响应。
开放告警仅保留安全元数据。日志或报告也不能证明服务完成了本次提交的新密钥分析；
实际接口未提供这一证明时须保留该边界。

本地审计当前覆盖 NuGet 图；Python 清单、Qt/编译器和冻结 OCR 原生载荷仍需对应的
依赖平台告警、清单、版本、许可及实际产物验证，不能因 NuGet 通过就声称全栈无漏洞。
代码扫描未配置时报告 `NOT_CONFIGURED`，不会擅自启用新服务或扩大权限。

报告是一次检查的证据，不会代替平台分支保护；本地文件仍需提交并推送后，新的 CI
和必需检查才能在远端实际生效。发布后还需读回身份并完整下载附件核对 SHA256。
