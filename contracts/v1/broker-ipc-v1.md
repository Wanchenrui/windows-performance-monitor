# Broker IPC v1

本协议只用于非提权 Agent 到可选 Broker 的本机通信。Desktop 不直接连接
Broker。产品 contract 版本与 Broker 协议版本独立；首版均写作 `1.0`。

## Endpoint and transport identity

Win32 endpoint：

```text
\\.\pipe\PerfMonitor.Broker.v1
```

Pipe DACL 禁止继承、拒绝 Network SID，并允许机器策略候选的本机
authenticated users 连接。DACL 只是第一层；Broker 必须通过 Pipe
impersonation 和客户端进程查询取得真实 SID/PID/映像，再执行机器策略。

请求不得包含 `callerSid`、`clientPid`、`clientPath` 或等价身份字段。
出现这些字段时按未知执行字段拒绝，而不是忽略。

## Framing and limits

消息为 4 字节无符号小端长度前缀加 UTF-8 JSON，长度不包含前缀。

- 最大消息：262,144 bytes；
- JSON 最大深度：24；
- hello deadline：5 s；
- action deadline：15 s；
- 每连接最多 1,024 个唯一 request ID；
- request/idempotency ID 最长 128 个 ASCII 字符；
- 未知字段默认允许用于非执行元数据的向后兼容，但任何名称匹配 path、
  command、argument、script、powershell、registry、environment 或 GUID
  的执行字段都必须拒绝。

## Hello

```json
{
  "type": "hello",
  "requestId": "agent-generated-id",
  "supportedBrokerProtocolVersions": ["1.0"],
  "maxMessageSize": 262144
}
```

Broker 返回：

```json
{
  "type": "helloAck",
  "requestId": "agent-generated-id",
  "selectedBrokerProtocolVersion": "1.0",
  "brokerInstanceId": "random-per-start-id",
  "maxMessageSize": 262144,
  "capabilities": {
    "dryRunOnly": true,
    "actions": []
  }
}
```

capabilities 是提示，不是授权承诺。每次执行仍重新读取策略和目标状态。

## Execute action

公共 envelope：

```json
{
  "type": "executeAction",
  "requestId": "agent-generated-id",
  "idempotencyKey": "stable-key-for-one-user-intent",
  "deadlineUtc": "2026-07-30T06:00:15.000Z",
  "dryRun": true,
  "action": {
    "actionType": "set_process_priority",
    "pid": 4242,
    "creationTimeTicks": 133000000000000000,
    "priority": "below_normal"
  }
}
```

`deadlineUtc` 必须是 UTC、晚于 Broker 接收时间且不超过 15 秒。Broker
使用自己的 UTC 时钟和有界内部 deadline；客户端时间不能延长上限。

四类 action body：

```json
{
  "actionType": "set_process_priority",
  "pid": 4242,
  "creationTimeTicks": 133000000000000000,
  "priority": "below_normal"
}
```

```json
{
  "actionType": "terminate_process",
  "pid": 4242,
  "creationTimeTicks": 133000000000000000
}
```

```json
{
  "actionType": "start_approved_diagnostic",
  "diagnosticId": "approved-id"
}
```

```json
{
  "actionType": "apply_approved_power_profile",
  "powerProfileId": "approved-id"
}
```

优先级枚举仅为 `idle`、`below_normal`、`normal`、`above_normal`。
PID 为 1～4,294,967,295，creation ticks 必须为正数。批准 ID 使用
`[a-z0-9][a-z0-9._-]{0,63}`。

## Response

```json
{
  "type": "actionResult",
  "requestId": "agent-generated-id",
  "brokerInstanceId": "random-per-start-id",
  "actionId": "broker-generated-id",
  "idempotencyKey": "stable-key-for-one-user-intent",
  "status": "dry_run",
  "errorCode": null,
  "receivedAtUtc": "2026-07-30T06:00:00.010Z",
  "completedAtUtc": "2026-07-30T06:00:00.020Z",
  "before": {},
  "after": {}
}
```

状态固定为：

- `succeeded`
- `dry_run`
- `denied`
- `rejected`
- `failed`
- `indeterminate`
- `idempotency_conflict`

稳定错误码至少包括：

- `invalid_request`
- `message_too_large`
- `protocol_version_unsupported`
- `request_timed_out`
- `action_not_supported`
- `action_policy_denied`
- `caller_identity_denied`
- `client_image_denied`
- `target_not_found`
- `target_identity_changed`
- `target_owner_mismatch`
- `target_protected`
- `idempotency_conflict`
- `idempotency_indeterminate`
- `audit_unavailable`
- `executor_failed`
- `service_unavailable`

错误信息不得包含命令行、用户目录、未经脱敏的映像路径或异常堆栈。

## Idempotency invariant

Broker 以实际 caller SID 和 `idempotencyKey` 建立唯一行，并存储规范化
action envelope 的 SHA-256。终态重取只返回已存结果。`pending` 或
`indeterminate` 永不重放 executor；不同摘要复用同一键永远冲突。

