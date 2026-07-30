# ADR-011：特权 Broker 与白名单动作

- 状态：Accepted
- 目标版本：0.7.2
- 日期：2026-07-30
- 实现状态：设计已冻结，实现待后续提交

## 结论

系统修改不得在 Desktop、非提权 Agent 或 Provider Worker 中执行。可选的
`PerfMonitor.Broker` 以 Windows 服务边界运行，只接受编译期固定 DTO
表示的四类动作：

- `SetProcessPriority`
- `TerminateProcess`
- `StartApprovedDiagnostic`
- `ApplyApprovedPowerProfile`

协议不提供 `RunCommand`、`RunPowerShell`、`ExecuteScript`、
`WriteArbitraryRegistry`，也不接收可执行文件路径、命令行、脚本文本、
注册表路径或任意电源计划 GUID。

Broker 缺失、未配置或不可用时，只把动作 capability 降级为不可用；采样、
历史、诊断和 Desktop 只读功能必须继续运行。

## 信任边界

正常调用链固定为：

```text
Desktop
  -> 当前用户 Agent Pipe
  -> Agent per-user policy
  -> Broker global local Pipe
  -> Broker machine policy
  -> OS action executor
  -> Broker-owned audit database
```

Desktop 不直接连接 Broker。Agent 的批准不是授权令牌；Broker 必须独立
执行全部参数、主体、目标、策略和资源校验。

Broker 从 Named Pipe 传输层取得：

- 实际客户端 SID；
- 实际客户端 PID；
- 客户端进程映像路径和 SHA-256。

请求体没有 `callerSid`、`clientPid` 或 `clientPath` 字段。服务模式必须把
客户端映像与管理员写入的受保护 Agent 路径和哈希进行匹配；不能把
`instanceId`、请求字段或同用户 SID 单独当成 Agent 身份。

Broker Pipe 禁止继承并显式拒绝 Network SID。只有本机经过身份校验的
客户端可以继续。服务模式要求进程令牌为 LocalSystem；用于 CI 和开发的
console 模式强制 `dry-run-only`，不得执行系统修改。

## 双重策略

Agent 持有当前用户的动作策略，Broker 持有机器级策略。一次动作必须同时
满足：

\[
\mathrm{Allow}
=
\mathrm{AgentPolicy}
\land
\mathrm{BrokerPolicy}
\land
\mathrm{TransportIdentity}
\land
\mathrm{TargetInvariant}
\]

任一条件不成立都返回稳定拒绝码，不调用 OS executor。

默认策略为 `dryRunOnly=true`，四类动作均未启用。管理员必须在受保护的
机器策略中显式启用动作、调用者 SID、Agent 映像哈希及允许的诊断或电源
配置；用户策略只能进一步收紧，不能扩大机器策略。

### 进程动作

进程目标必须同时携带 `pid` 和精确的 `creationTimeTicks`。Broker 在执行
前重新打开进程并验证：

1. PID 仍存在；
2. 创建时间精确相等，防止 PID 复用；
3. 目标所有者 SID 等于实际调用者 SID；
4. 目标不是 PID 0/4、Broker、Agent、受保护进程或策略拒绝项。

优先级只允许 `idle`、`below_normal`、`normal`、`above_normal`。首版不
允许 `high` 或 `real_time`。

### 诊断与电源动作

请求只能携带稳定的 `diagnosticId` 或 `powerProfileId`。Broker 在机器
策略中把 ID 映射到管理员批准的固定定义；请求不能覆盖路径、参数或 GUID。
电源计划通过固定 Win32 API 调用，不启动 `powercfg` 或 shell。

## 幂等与崩溃语义

操作系统状态修改和 SQLite 提交不可能组成同一个原子事务，因此本版本不
声称 exactly-once。保证是持久化的 at-most-once：

1. 规范化请求并计算 SHA-256；
2. 在 Broker 自有 SQLite 中提交 `pending` 审计行；
3. 读取并记录执行前状态；
4. dry-run 只返回计划结果；非 dry-run 才调用 OS executor；
5. 读取执行后状态并把同一行更新为终态。

幂等作用域是 `(callerSid, idempotencyKey)`：

- 相同键和相同请求哈希已完成：返回原结果，不重放；
- 相同键但请求哈希不同：`idempotency_conflict`；
- 发现先前 `pending`：启动恢复时转为 `indeterminate`，永不自动重放；
- `indeterminate`：返回原状态，要求人工核对目标和审计记录。

这一模型优先避免重复终止进程或重复修改机器状态。它不能在进程恰好崩溃
于 OS 修改之后、审计终态提交之前时证明动作是否成功，因此必须显式报告
未知，而不是猜测。

## 审计

每次批准、拒绝、失败、dry-run 和不确定结果都记录：

- action/idempotency/request hash；
- 传输层 caller SID、客户端 PID、映像路径哈希；
- Agent 与 Broker 策略版本；
- 动作类型和有界、脱敏参数；
- Broker 接收/开始/完成 UTC 时间；
- 执行前状态、执行后状态；
- 结果状态和稳定错误码。

审计库位于 `%ProgramData%\PerfMonitor\broker\broker-v1.db`，由 Broker
账户写入。新动作必须先成功持久化 `pending`；审计不可写时 fail closed，
不执行系统修改。审计查询有条数、时间范围和 payload 大小上限。

## 时序和资源边界

- Broker 协议最大消息 256 KiB，分配前检查；
- hello 期限 5 秒，动作期限 15 秒；
- 最多 16 个 Pipe 客户端；
- OS 修改通过单一有界执行闸串行化；
- 每个连接最多 1,024 个唯一 request ID；
- 参数、状态和审计 JSON 都有确定长度上限；
- 客户端断开会取消尚未开始的请求；已经进入 OS executor 的请求以审计
  状态为准，不能因断线自动重试。

Broker 调度不占用 Provider scheduler 槽，也不位于实时快照、SQLite 历史
或诊断事件流水线上。

## 回退

v0.7.2 不修改用户历史 SQLite schema。回退时先停用并卸载 Broker 服务，
再回退 Agent/Desktop 到 v0.7.1；保留 Broker 审计库作为安全记录，不把它
交给旧版本写入。没有安装 Broker 的部署可直接回退二进制。

