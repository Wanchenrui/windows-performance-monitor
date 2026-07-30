# 电脑性能监控

当前版本为 `0.5.0`。产品运行路径已经切换为按用户运行的
`.NET 10 PerfMonitor.Agent`、独立 WPF Desktop、受保护 Named Pipe IPC
和 SQLite 历史库。Python 0.3 源码继续保留为指标口径 oracle、golden
fixture 生成器和差分基线，但不再作为默认或发布版高频 Agent。

## v0.5 架构边界

```text
Windows Providers
  -> absolute-deadline scheduler
  -> immutable latest snapshot
     -> latest-wins Named Pipe subscription -> Desktop
     -> bounded queue -> single SQLite writer
```

- `PerfMonitor.Agent` 默认以当前用户、非管理员权限运行。Provider 使用独立
  周期、绝对期限、超时、同 Provider 不重入、有界并发和失败退避。
- `PerfMonitor.Desktop` 是独立进程，只通过 contract v1 IPC 读取实时快照、
  能力和有界历史；关闭或异常退出 Desktop 不会停止 Agent。
- IPC 使用当前用户 SID 派生的
  `\\.\pipe\PerfMonitor.<SID>.agent`。DACL 禁止继承，只允许当前用户与
  LocalSystem，并显式拒绝 Network SID。
- SQLite 位于 `%LOCALAPPDATA%\PerfMonitor\data\history-v1.db`。采样线程
  只做非阻塞 `TryWrite`；队列满或存储失败时增加丢弃计数，实时快照继续。
- GPU、网络、温度、告警、自动优化和特权 Broker 不属于 v0.5。

## 构建与运行

开发/构建要求为 Windows 10/11、.NET SDK `10.0.302` 和 64 位
Python 3.12。Python 只用于 oracle 测试和差分验证；已构建产品运行时不调用
Python。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup.ps1
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
.\启动.bat
```

`启动.bat` 默认启动隐藏的 Agent 和 Desktop。Desktop 关闭后 Agent 继续
采样和写历史；再次运行脚本时，Desktop 会连接到现有的当前用户 Agent。
需要显式停止 Agent 时：

```powershell
Get-Process perf-monitor-agent -ErrorAction SilentlyContinue |
  Stop-Process
```

浏览器 HTTP 仅保留为显式开发模式，不进入产品构建：

```powershell
.\启动.bat --dev-http
```

需要给 Python oracle 传入端口或无托盘等参数时，应直接执行：

```powershell
.\.venv\Scripts\python.exe .\app.py --no-tray --port 7700
```

## IPC 契约与安全

消息格式为 4 字节无符号小端长度前缀加 UTF-8 JSON。分配 payload 前先
验证长度，硬上限为 4 MiB，JSON 最大深度为 32。客户端首先发送 `hello`
并协商独立的 `contractVersion=1.0`，响应包含产品版本、随机
`instanceId`、能力和消息上限。

v1 支持：

- `getSnapshot`
- `getCapabilities`
- `queryHistory`
- `subscribe`
- `unsubscribe`

每个连接最多 4,096 个唯一 `requestId`，hello 超时 5 秒，历史请求超时
10 秒。实时订阅使用容量 1 的 latest-wins 队列，慢 Desktop 不会反压采样。
`instanceId + sequence` 只用于识别 Agent 重启，不是认证凭据。完整协议见
`contracts/v1/ipc-v1.md`。

## SQLite 历史

SQLite 使用单写者、批量事务、WAL、`synchronous=NORMAL`、1 秒
`busy_timeout`，读写连接分离。启动时执行 `integrity_check(1)`、被动 WAL
checkpoint 和显式 schema migration。

默认保留策略：

| 数据层 | 保留时间 | 用途 |
|---|---:|---|
| 1 秒 raw | 48 小时 | 短期细节 |
| 1 分钟 rollup | 30 天 | 中期趋势 |
| 1 小时 rollup | 366 天 | 长期趋势 |

rollup 保存 `min/max/avg/last/count`。历史请求必须提供 `from/to`，最多
16 个白名单指标、5,000 点和 366 天范围；SQL 侧降采样保留尖峰，不把全量
历史搬入 Agent 内存。默认不采集或持久化完整路径、命令行或脚本文本。

## 指标口径

系统 CPU 为 Windows 系统 CPU 时间得到的 0～100% 整机负载。进程 CPU
同时提供整机归一化和核心等效口径：

\[
U_{p,\mathrm{normalized}}
=100\%\times
\frac{\Delta CPUTime_p}
{\Delta t\times N_{\mathrm{logical}}}
\]

\[
U_{p,\mathrm{coreEquivalent}}
=100\%\times
\frac{\Delta CPUTime_p}{\Delta t}
\]

进程差分身份是 `(PID, creationTimeTicks)`，避免 PID 复用污染 CPU 基线。
首次出现的进程没有差分窗，CPU 值保持 `null`，不会伪造为 0。

进程内存同时提供：

- `workingSetBytes`：当前驻留物理内存工作集；
- `privateBytes`：进程私有提交字节数，不可得时为 `null`。

系统内存使用量为 `total - available`。系统盘从 Windows 卷信息识别，不在
业务代码中固定为 C 盘。每个指标组分别表达 availability、freshness 和
coverage；权限不足、瞬时进程退出或 Provider 失败不会被映射为数值 0。

## 验证

```powershell
.\.venv\Scripts\python.exe -m pytest
dotnet restore .\PerfMonitor.slnx --locked-mode
dotnet test .\PerfMonitor.slnx --configuration Release --no-restore
```

测试覆盖契约 Schema、golden fixtures、Provider 调度和故障隔离、
259,200 周期虚拟长稳、IPC framing/ACL/版本协商、Desktop 断线保留与
Agent 重启识别、SQLite 有界查询/尖峰保留和存储失败降级。

Python/.NET 同窗差分：

```powershell
.\scripts\compare_agents.ps1 `
  -AgentPath .\dist\agent\perf-monitor-agent.exe `
  -Samples 20 `
  -OutputPath .\artifacts\agent-differential.json
```

CPU/内存均值绝对差默认不得超过 3/1 个百分点；容量、单位、source ID、
空值和进程身份做精确语义比较。脚本从 `Directory.Build.props` 读取 Agent
版本，避免发布门禁与产品版本漂移。

真实 72 小时发布长稳仍使用同一可执行门禁：

```powershell
.\scripts\run_agent_soak.ps1 `
  -AgentPath .\dist\agent\perf-monitor-agent.exe `
  -OutputPath .\artifacts\agent-soak-72h.json
```

短时 CI 和 259,200 次虚拟推进不能替代
`release72HourGate.actualWallClockPassed=true` 的真实墙钟证据。

## 发布产物

`scripts\build.ps1` 先运行 Python oracle 与 .NET 全部测试，再以锁定依赖
发布：

```text
dist/agent/perf-monitor-agent.exe
dist/desktop/perf-monitor-desktop.exe
dist/build-manifest.json
```

manifest 记录全部 Agent/Desktop 文件的 SHA-256 与大小、Git commit/dirty
状态、所有 Python/.NET 锁文件哈希、.NET SDK、Python oracle 版本和构建
环境。当前产物为 .NET 10 framework-dependent；1.0 的安装包、代码签名、
SBOM 和来源证明另按发布门禁实现。

`scripts/smoke.ps1` 会启动真实 Agent、SQLite 和 Desktop，关闭 Desktop 后
确认 Agent 序列继续增长，再等待 Agent 正常退出并校验数据库。

## 模块边界

```text
src/PerfMonitor.Contracts/          contract v1 DTO、稳定 ID 与历史上限
src/PerfMonitor.Core/               Provider 契约、调度、原子快照与 fan-out
src/PerfMonitor.Collectors.Windows/ 非提权 Windows Provider
src/PerfMonitor.Ipc.NamedPipes/     安全 framing、ACL、Server/Client 与订阅
src/PerfMonitor.Storage.Sqlite/     migration、单写者、retention 与 rollup
src/PerfMonitor.Agent/              产品 Agent 生命周期与查询适配
src/PerfMonitor.Desktop/            独立 WPF 客户端、重连与最新状态保留
perf_monitor/                       只读 Python 指标 oracle
contracts/v1/                       Schema、目录、IPC 文档与 fixtures
scripts/compare_agents.ps1          Python/.NET 同窗差分
scripts/run_agent_soak.ps1          实际资源与 72 小时发布门禁
```

## 回退与已知风险

- v0.5 二进制可回退到 `v0.4.0`；v0.4 不读取 SQLite schema v1。回退不得
  删除 `%LOCALAPPDATA%\PerfMonitor\data`，以便重新升级后恢复历史。
- migration 或写入失败会降级历史能力，但不会停止实时采样。状态目前通过
  稳定错误码暴露，完整诊断事件在 v0.6 实现。
- Named Pipe DACL 的结构在 CI 中验证；跨用户实机矩阵仍需在 1.0 安装/
  升级验证环境复测。
- 厂商 GPU/温度 SDK 可能阻塞或崩溃，v0.7 前不会放入 Agent 核心。
