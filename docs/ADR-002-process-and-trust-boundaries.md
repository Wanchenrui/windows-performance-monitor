# ADR-002：进程模型与权限边界

- 状态：已采用
- 日期：2026-07-29
- 首次适用版本：0.3.0
- 实现状态：Agent/Desktop 边界在 0.5.0 实现；Broker/Worker 待 0.7.x

## 决策

最终产品由四类进程组成：

1. `PerfMonitor.Agent`：按用户运行、默认不提权，负责采集、调度、快照、
   持久化与策略判断。
2. `PerfMonitor.Desktop`：独立 UI 进程，只通过版本化 IPC 使用 Agent。
3. `PerfMonitor.Broker`：可选 Windows 服务，仅执行固定白名单的特权动作。
4. Provider Worker：隔离可能阻塞或崩溃的厂商 GPU/传感器 SDK。

Agent 是模块化单体，不拆微服务。Desktop 不直接访问 Win32、SQLite 或
Broker；Broker 不接受任意命令行、脚本、PowerShell、注册表路径或文件路径。

0.5 的 Desktop 使用原生 WPF 首版界面，不引用 Windows Collector 或
SQLite 项目；其唯一产品数据入口是受版本控制的 Named Pipe Client。

## 信任边界

- Agent 与 Desktop 的 Named Pipe DACL 绑定当前用户 SID。
- 多用户会话拥有不同的 Pipe、实例状态和数据库；跨用户连接必须失败。
- Broker 再次验证调用者 SID、命令 ID、参数范围、幂等键和策略，不信任
  Agent 传来的任意字符串。
- Broker 缺失或停止只禁用特权动作，不影响监控、历史和诊断。
- Worker 崩溃只使对应 capability unavailable，Agent 可终止并重启 Worker。

## 允许的 Broker 形态

允许 `SetProcessPriority`、`TerminateProcess`、
`StartApprovedDiagnostic`、`ApplyApprovedPowerProfile` 等显式 DTO。
每次动作记录调用者 SID、动作 ID、参数摘要、前后状态、结果和时间。

## 后果与回退

进程数量增加，但 UI、硬件 SDK 和特权故障被隔离。0.3 只冻结边界，不安装
Broker；在 Broker 尚未实现时所有动作 capability 必须报告不可用。
