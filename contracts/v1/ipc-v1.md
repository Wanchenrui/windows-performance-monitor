# IPC v1 framing and negotiation

目标传输为当前用户专属的 Windows Named Pipe。逻辑层级
`PerfMonitor/<UserSid>/agent` 在 Win32 对象名中规范化为：

```text
\\.\pipe\PerfMonitor.<UserSid>.agent
```

`PipeName` 本身不允许反斜杠；点分名称与逻辑层级是一对一映射。

## Framing

每个消息由 4 字节无符号小端长度和 UTF-8 JSON 组成。长度只包含 JSON
字节，不包含前缀。Agent 在分配消息缓冲区前拒绝超过协商
`maxMessageSize` 的长度；首版硬上限为 4 MiB。

## Hello

客户端连接后必须先发送：

```json
{
  "type": "hello",
  "requestId": "client-generated-id",
  "supportedContractVersions": ["1.0"],
  "maxMessageSize": 4194304
}
```

Agent 返回选定契约版本、`instanceId`、能力和服务端限制。没有共同版本时
返回稳定错误 `contract_version_unsupported` 并关闭连接。

hello 必须在 5 秒内完成。客户端声明的 `maxMessageSize` 必须在 16 KiB～
4 MiB；服务端选择双方较小值。最多声明 8 个契约版本。

## Request types

- `getSnapshot`
- `queryHistory`
- `getCapabilities`
- `getHealth`
- `subscribe`
- `unsubscribe`

所有请求必须带唯一 `requestId`，响应回显该值。请求有明确超时；断开连接
必须取消该连接的订阅和未开始工作。

每连接最多处理 4,096 个 request ID，单个 ID 最长 128 字符。
`queryHistory` 的 deadline 为 10 秒，并必须同时包含：

```json
{
  "type": "queryHistory",
  "requestId": "client-generated-id",
  "metricIds": ["system.cpu.utilization.percent"],
  "fromEpochMs": 1785312000000,
  "toEpochMs": 1785398400000,
  "maxPoints": 240
}
```

历史范围最多 366 天，指标最多 16 个，`maxPoints` 为 1～5,000。订阅只允许
每连接一个，服务端先返回 `subscribed`，再用相同 request ID 发送
`snapshotUpdate`。订阅缓冲区容量为 1，慢客户端只收到最新快照。

错误响应：

```json
{
  "type": "error",
  "requestId": "client-generated-id",
  "instanceId": "agent-instance-id",
  "error": {
    "errorCode": "invalid_request"
  }
}
```

IPC 稳定错误包括 `contract_version_unsupported`、`invalid_request`、
`message_too_large`、`request_timed_out` 和 `service_unavailable`。

## Security invariants

- Pipe DACL 禁止继承，只允许当前用户 SID 和 LocalSystem，并显式拒绝
  Network SID；
- 首个服务端句柄要求 `FirstPipeInstance`，同名对象预占会使 Agent 启动
  失败，而不是降级到不受信任对象；
- Desktop 不直接连接 Broker；
- IPC 不接受命令行、PowerShell 或脚本文本；
- 长度、JSON 深度、数组大小、历史时间范围和 `maxPoints` 都在进入业务层
  前验证；
- `instanceId + sequence` 用于识别 Agent 重启，不能作为授权令牌。

首版最多同时接受 8 个客户端。JSON 最大深度为 32；请求字段、数组长度和
历史上限均在进入 SQLite 查询层前验证。
