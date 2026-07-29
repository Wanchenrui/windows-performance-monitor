# ADR-004：Provider 调度与故障隔离

- 状态：已采用
- 日期：2026-07-29
- 目标实现：0.4.0

## Provider 接口

每个 Provider 声明：

```text
ProviderId
DefaultPeriod
Timeout
RequiredPrivilege
CostClass
Capability
CollectAsync(cancellationToken)
```

首批周期为 CPU/内存 1 秒、进程 2 秒、卷容量 30 秒、硬件清单 5 分钟或
事件触发。诊断模式只提高指定对象的频率，不提高全进程枚举频率。

## 调度规则

- 每个 Provider 使用独立单调时钟 epoch 和绝对期限；
- 采用有界并发，不在线程池中无界排队；
- 同一 Provider 上一次尚未完成时跳过新触发，不重入、不累积任务；
- 超时或异常只降低该指标组，不阻塞快照中其他组；
- 连续失败按有上限的指数退避，成功后重置；
- 系统恢复时重建 epoch，并记录 `system_suspend` gap；
- 供应商 SDK 放 Worker，超时可终止整个 Worker。

## 快照

Snapshot Assembler 使用每个 Provider 最近结果组装不可变快照。组装不等待
慢 Provider；超龄结果标记 `stale`。发布以单次引用替换完成，读者不会混合
两个 sequence。

## 时序预算

Provider 的执行时间、排队时间、超时、跳过和失败均进入 Self Metrics。
新增 Provider 必须给出 P50/P95/P99 成本及默认周期依据，不能凭经验假定
调用耗时。
