# ADR-003：指标、质量、时间与错误契约

- 状态：已采用
- 日期：2026-07-29
- 契约：1.0

## 决策

产品版本与契约版本分离。0.3.0 首次发布 `contractVersion=1.0`，后续
0.x/1.x 产品可以继续使用该契约。兼容新增字段不提升主契约版本；删除字段、
改变单位、复用 ID 或改变既有语义必须发布新主版本。

每个指标有稳定 `metricId`、`unit` 和 `sourceId`；每个组有稳定
`providerId` 与 `observedAtUtc`。质量分为三个正交维度：

```text
availability:
  available | partial | unavailable | not_supported |
  permission_denied | timeout | error

freshness:
  warming_up | fresh | stale

coverage:
  complete | limited
  + enumerated/readable/skipped/skippedByReason
```

## 时间

快照同时记录 `scheduledAtUtc`、`startedAtUtc`、`completedAtUtc` 和每个
Provider 的 `observedAtUtc`。调度仍使用单调时钟；UTC 用于跨进程展示、
持久化和查询。

2026-10-02 的 WP-03 以兼容字段补齐实例内时间：快照 `elapsedSeconds`
为从 Assembler 创建起点计量的单调经过秒数；组 `observedElapsedSeconds`
使用同一起点，`observationSequence` 是该组新观测发布时的全局快照序号。
保持值沿用原观测身份，其他 Provider 发布新帧不会增加该组的独立证据。
Scheduler 的本地开始/完成 timestamp 只在进程内传递，不持久化成跨实例
可比较的时钟值。

快照 `sequence` 计 Provider 更新，不能据其跳号推断丢帧。
`deliverySequence` 由现有 SnapshotFanout 在每次实际交付时赋值，所有
消费者收到同一 envelope；其缺口才表示该消费者漏掉了交付。
年龄、新鲜度、诊断持续时间、冷却与证据窗口只使用实例内单调时间。
`instanceId` 变更重置观察状态，交付缺口或超过规则上限的观测时间缺口
打断连续证据。UTC 前后跳不会改变状态转换；UTC 的 first/last 标签仍
表示逻辑先后，墙上时钟回拨时数值不一定递增。睡眠恢复以单调观察缺口
处理，本轮不新增自动动作或通用 `gapReason` 状态。

## 错误

公开响应只使用 `error-codes.json` 中的稳定机器码。Python/C# 异常类名和
运行时消息不得成为兼容逻辑；中文展示由 UI 本地映射。原生诊断细节只能
进入受控日志或脱敏诊断包。

## 兼容规则

- 已定义字段不可用时返回 `null` 并设置质量状态，不能填 0。
- 客户端必须忽略未知字段和未知指标组。
- 服务端不得在同一 `metricId` 下改变单位或归一化公式。
- Golden fixtures、JSON Schema、Python 输出和 .NET DTO 共同构成门禁。
