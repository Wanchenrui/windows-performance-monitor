# IPC v1 framing and negotiation

目标传输为当前用户专属的 Windows Named Pipe：

```text
\\.\pipe\PerfMonitor\<UserSid>\agent
```

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

## Request types

- `getSnapshot`
- `queryHistory`
- `getCapabilities`
- `subscribe`
- `unsubscribe`

所有请求必须带唯一 `requestId`，响应回显该值。请求有明确超时；断开连接
必须取消该连接的订阅和未开始工作。

## Security invariants

- Pipe DACL 只允许当前用户 SID 和 LocalSystem；
- Desktop 不直接连接 Broker；
- IPC 不接受命令行、PowerShell 或脚本文本；
- 长度、JSON 深度、数组大小、历史时间范围和 `maxPoints` 都在进入业务层
  前验证；
- `instanceId + sequence` 用于识别 Agent 重启，不能作为授权令牌。
