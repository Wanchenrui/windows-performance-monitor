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
持久化和查询。睡眠恢复、时钟调整和 Provider 超时在后续调度器中以显式
`gapReason` 表达，不用普通 overrun 代替。

## 错误

公开响应只使用 `error-codes.json` 中的稳定机器码。Python/C# 异常类名和
运行时消息不得成为兼容逻辑；中文展示由 UI 本地映射。原生诊断细节只能
进入受控日志或脱敏诊断包。

## 兼容规则

- 已定义字段不可用时返回 `null` 并设置质量状态，不能填 0。
- 客户端必须忽略未知字段和未知指标组。
- 服务端不得在同一 `metricId` 下改变单位或归一化公式。
- Golden fixtures、JSON Schema、Python 输出和 .NET DTO 共同构成门禁。
