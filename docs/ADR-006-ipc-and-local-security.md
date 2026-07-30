# ADR-006：IPC 与本地安全模型

- 状态：已采用
- 日期：2026-07-29
- 目标实现：0.5.0
- 实现状态：0.5.0 已实现

## 决策

Agent 与 Desktop 使用当前用户 SID 专属 Windows Named Pipe。首版协议为
4 字节小端长度前缀加 UTF-8 JSON，详见 `contracts/v1/ipc-v1.md`。选择该
形式是为了可调试性和明确边界，不在当前数据量下提前引入复杂 RPC。

逻辑命名空间为 `PerfMonitor/<SID>/agent`。Win32 的 `PipeName` 参数不能
包含反斜杠，因此实际对象名规范化为
`\\.\pipe\PerfMonitor.<SID>.agent`；两者是一对一映射，不降低多用户隔离。

协议必须支持 hello/版本协商、`instanceId`、`requestId`、
`maxMessageSize`、超时、订阅、快照、历史和能力查询。

## 安全控制

- Pipe DACL 只允许当前用户 SID 与 LocalSystem；
- DACL 禁止继承并显式拒绝 Network SID；首个监听句柄使用
  `FirstPipeInstance` 防止同名对象抢占；
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

## 0.5 运行上限

- 最多 8 个并发 Pipe 实例；
- hello 超时 5 秒，业务请求超时 10 秒；
- 每连接最多 4,096 个唯一 request ID，request ID 最长 128 字符；
- 客户端最多声明 8 个契约版本；
- 协商消息上限不低于 16 KiB，绝对上限 4 MiB；
- JSON 最大深度 32；
- 订阅队列容量为 1，慢客户端只保留最新快照。

实现使用显式 `PipeSecurity` 同时授权当前用户与 LocalSystem，因此不使用会
忽略自定义 `PipeSecurity` 的 `PipeOptions.CurrentUserOnly`。跨用户拒绝由
受保护 DACL 完成，`instanceId` 不参与授权。
