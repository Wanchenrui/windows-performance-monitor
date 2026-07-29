# ADR-006：IPC 与本地安全模型

- 状态：已采用
- 日期：2026-07-29
- 目标实现：0.5.0

## 决策

Agent 与 Desktop 使用当前用户 SID 专属 Windows Named Pipe。首版协议为
4 字节小端长度前缀加 UTF-8 JSON，详见 `contracts/v1/ipc-v1.md`。选择该
形式是为了可调试性和明确边界，不在当前数据量下提前引入复杂 RPC。

协议必须支持 hello/版本协商、`instanceId`、`requestId`、
`maxMessageSize`、超时、订阅、快照、历史和能力查询。

## 安全控制

- Pipe DACL 只允许当前用户 SID 与 LocalSystem；
- 消息长度在分配前检查，首版最大 4 MiB；
- JSON 深度、数组长度、历史范围和点数有硬上限；
- 每个请求有 deadline 与取消传播；
- `instanceId` 只用于重启识别，不用于认证；
- 控制动作仍由 Agent 策略层和 Broker 双重验证。

## HTTP

Python 0.3 的回环 HTTP 仅作为参考实现。0.5 起浏览器 HTTP 模式默认关闭，
只作为显式开发选项。端口监听者返回相同 `service` 字符串不能构成实例
身份；兼容期继续要求命名互斥体、当前用户状态文件和随机 `instanceId`
同时匹配。
