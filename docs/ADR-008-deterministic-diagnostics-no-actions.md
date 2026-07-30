# ADR-008：确定性诊断与无动作边界

- 状态：已接受
- 目标版本：0.6.0

## 决策

v0.6 只解释已采集事实，不执行修复。所有规则是以
`(instanceId, ruleId, ruleVersion, subjectId)` 为键的有限状态机，按单个
有界队列串行处理。状态转换只使用快照或 Provider 的 UTC 时间；线程调度
延迟和 evaluator 墙钟不能改变输出。

每个输入观察被分类为 breach、recovery 或 hysteresis band：

\[
\mathrm{inactive}\xrightarrow[
  t-t_\mathrm{breach}\ge D_a
]{x\ge T_a}\mathrm{active}
\]

\[
\mathrm{active}\xrightarrow[
  t-t_\mathrm{recovery}\ge D_r
]{x\le T_r}\mathrm{resolved}
\]

其中 \(T_a>T_r\)，\(D_a,D_r\ge0\)。相邻有效观察超过规则的连续性上限时，
pending debounce 被清除，不推断缺失区间内条件持续成立。cooldown 只抑制
短时间内新 episode 的重复通知，不修改当前活动状态。

## 存储边界

- 策略 JSON：持久层；
- pending/active/cooldown 与最近事件：有界 RAM；
- snapshot 与诊断事件：既有 SQLite 单 writer；
- Desktop：只通过 Named Pipe 查询，不直接读策略或数据库。

策略损坏时采用 RAM 默认策略并报告稳定警告码。该降级不能停止采样。
配置进程规则只接受经过长度、重复和路径分隔符校验的进程名。

## 确定性证据

事件 ID 是实例、规则、版本、主体、状态、firstSeen 和 lastSeen 的
SHA-256。raw snapshot 按 `sample_time, instance_id, sequence` 顺序读取；
重放若检测到逆序则拒绝。重放范围最多 48 小时和 200,000 个快照，避免以
“确定性”为由建立无界内存路径。

## 安全边界

capabilities 固定 `actionsSupported=false`。IPC 不定义 action、command、
PowerShell、script、registry 或任意文件写请求。需要特权动作的最小 Broker
属于 v0.7.x，必须另行建立白名单、调用者 SID、dry-run、幂等与审计模型。
