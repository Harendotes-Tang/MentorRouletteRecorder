# Collector 集成测试

运行真实的命名管道服务端、真实的 SQLite 文件与真实的帧，
**不需要** Npcap、不需要游戏、不需要网络。

每个测试用例使用各自的临时数据库与管道名，因此可并行执行，
且不会触及开发机上真实的 `%LOCALAPPDATA%\MentorRecorder\mentor_recorder.db`。

## 覆盖内容

`PipeServerTests` —— 端到端的线协议：

- `GetVersion` 握手；`GetStatus` 报告 schema 版本与如实的抓包状态；
- 抓包类消息的 Phase 1 占位答复（`ERR_NPCAP_MISSING` / `ERR_CAPTURE_NOT_RUNNING`，
  `npcap_installed = false`，`injected_hook_enabled = false`）；
- `QueryRuns` 分页（页间不重叠）与 `page_size > 200` 被拒；
- `CorrectRun` 与 `GetRunRevisions` 呈现完整修订链，创建时的原值仍可自 revision 1 读出；
- 同一 `request_id` 重发 `CorrectRun` 仅应用一次，`idempotent_replay = true`；
- 非法 JSON 帧被拒后连接**仍可用**；未知 `message_type` 显式报错；
  `protocol_version != 1` 返回 `ERR_PROTOCOL_VERSION`；超长帧关闭连接；
- 两个并发客户端互不影响；
- `SubscribeLiveEvents` 在一次变更后收到 `run_updated`；两个订阅者同时收到事件；
- 四张统计表；导出与备份写出真实文件；导出至用户目录之外被拒；
- 契约中**每一个** `message_type` 均有答复（载荷或明确的错误码）。

`ReplayIntegrationTests` —— 逐个重放固件，期望值依据
[`../../docs/state-machine.md`](../../docs/state-machine.md) 与
[`../../docs/statistics-definitions.md`](../../docs/statistics-definitions.md) **手算**后
写入 `Expectations` 表；另覆盖“重放两次不产生任何重复写入”。

`CrashRecoveryTests` —— 进程崩溃后重启：未完结的记录转为 `INTERRUPTED` 且
`pending_review = 1`，**绝不**转为 `COMPLETED`；追加 `PROCESS_RESTART` 事件与系统修订；
再次重启既不重复恢复，也不重复插入标记；仅人工 `CorrectRun` 可将其改为完成。
同一文件另覆盖 10 万条事件下的**有界**去重集合：容量恒定，尾部重放仍可识别为重复。

## 尚未覆盖

- 真实 Npcap 抓包与游戏客户端。集成测试通过 `CaptureFakes` 注入伪造的适配器与报文源；
  真实链路由 [`../../docs/live-validation-guide.md`](../../docs/live-validation-guide.md) 的真机验证覆盖。
  管道 ACL 的外部核对由 `LifecyclePipeSecurityTests` 覆盖，进程级优雅停止由 `LifecycleProcessTests` 覆盖。
