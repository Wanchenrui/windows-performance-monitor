# ADR-008：确定性诊断与无动作边界

- 状态：已接受
- 目标版本：0.6.0

## 决策

v0.6 只解释已采集事实，不执行修复。所有规则是以
`(instanceId, ruleId, ruleVersion, subjectId)` 为键的有限状态机，按单个
有界队列串行处理。2026-10-02 的 WP-03 将原 UTC 时长依据修正为快照的
`elapsedSeconds` 和组的 `observedElapsedSeconds`；线程调度延迟和 evaluator
墙钟不能改变输出。重复观察由组 `observationSequence` 识别，实际交付
缺口由 SnapshotFanout 的 `deliverySequence` 识别；Provider 更新序号
自然跳跃不能当成丢帧。UTC 仅用于展示和查询。

原规则 ID、单位、`process:名称` 最大 CPU 聚合主体均保留。名称包含的
PID＋创建时间集合以稳定指纹管理内部连续性；集合变化或确认退出结束
旧观察，不产生实测恢复事件。

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
pending debounce 被清除，不推断缺失区间内条件持续成立。交付丢失、
缺测、预热和 stale 也清除 pending 与旧证据。未知不填 0，不自动 resolved；
limited 进程覆盖可以证明已读到的高 CPU，不能证明名称最大 CPU 已恢复。
实例变更不继承状态和冷却。cooldown 只抑制
短时间内新 episode 的重复通知，不修改当前活动状态。

## 存储边界

- 策略 JSON：持久层；
- pending/active/cooldown 与最近事件：有界 RAM；
- snapshot 与诊断事件：既有 SQLite 单 writer；
- Desktop：只通过 Named Pipe 查询，不直接读策略或数据库。

策略损坏时采用 RAM 默认策略并报告稳定警告码。该降级不能停止采样。
配置进程规则只接受经过长度、重复和路径分隔符校验的进程名。

## 确定性证据

新事件 ID 是实例、规则、版本、主体、状态、episode 首次观察序号和当前
观察序号的 SHA-256；旧持久化事件 ID 保留。事件兼容新增
`observationSequence`，同实例当前状态按观察序号选择，查询按接收顺序
取最近事件，不能用回拨后的 UTC 值恢复旧 active 状态。旧实例和缺序号
事件仍可查，但不作为当前告警。

持久化回放按接收顺序读取；实例内以序号和单调时间验证先后，不能按 UTC
重排 Agent 重启边界。同步内存回放可按交付序号排序实例内输入，实例块
保留首次出现顺序且不能重入。逻辑逆序重放会拒绝。重放范围最多 48 小时
和 200,000 个快照，避免以
“确定性”为由建立无界内存路径。

旧 raw 历史缺少单调时间时保留历史和旧事件查询，诊断回放明确返回
`diagnostic_replay_time_unavailable`，不从 UTC 补造持续时间。最小规则投影
带版本与策略指纹；策略不兼容明确返回
`diagnostic_replay_projection_incompatible`，不会给出不完整的近似结论。

## 安全边界

capabilities 固定 `actionsSupported=false`。IPC 不定义 action、command、
PowerShell、script、registry 或任意文件写请求。需要特权动作的最小 Broker
属于 v0.7.x，必须另行建立白名单、调用者 SID、dry-run、幂等与审计模型。
