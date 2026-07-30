# 电脑性能监控

当前开发版本为 `0.7.1`。产品运行路径为按用户运行的
`.NET 10 PerfMonitor.Agent`、独立 WPF Desktop、受保护 Named Pipe IPC
和 SQLite 历史库，并包含只读确定性诊断、稳定 Windows 指标及隔离硬件
Worker。Python 0.3 源码继续
保留为指标口径 oracle、golden
fixture 生成器和差分基线，但不再作为默认或发布版高频 Agent。

## v0.7.1 架构边界

```text
stable Windows Providers
isolated GPU / temperature Worker
  -> absolute-deadline scheduler
  -> immutable latest snapshot
     -> latest-wins Named Pipe subscription -> Desktop
     -> bounded queue -> deterministic diagnostics
                         -> bounded RAM events
                         -> existing single SQLite writer
     -> bounded queue -> existing single SQLite writer
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
- 诊断规则使用快照中的逻辑时间，单线程确定性执行；同一组历史快照按固定
  顺序重放会产生相同事件 ID 与事件序列。
- v0.7.1 的 GPU/温度只读采集位于 `asInvoker` Worker。Agent 不引用
  厂商硬件库；命令执行、优化动作和特权 Broker 仍不存在。

## 稳定扩展指标

v0.7.0 只把可由受支持 Windows/.NET API 非提权读取的指标放入 Agent：

| 指标组 | 周期 | 物理口径 | 首样本 |
|---|---:|---|---|
| network | 1 s | 活动非回环接口累计字节的单调时间差分 | 速率为 `null` |
| diskIo | 1 s | PDH `PhysicalDisk(_Total)` 读写字节/操作速率 | 速率为 `null` |
| power | 5 s | `GetSystemPowerStatus` 的电源、电量、充电与节能状态 | 可立即读取 |

网络 Provider 逐接口保存 RAM 基线。新接口只建立基线；计数器回退视为复位，
该接口不参与本周期速率，因此不会产生虚假尖峰。累计口径包括活动虚拟接口，
所以它表示“网卡计数器总吞吐”，不是去重后的公网链路流量。

磁盘 I/O 使用 `PdhAddEnglishCounter`，不依赖操作系统显示语言。PDH 速率
需要两个样本，第一帧明确为未就绪而不是 0。电池不存在、状态未知和剩余时间
未知分别映射为 `batteryPresent=false` 或数值 `null`，不把未知物理量伪造
为 0。

## 隔离 GPU 与温度指标

v0.7.1 固定使用 `LibreHardwareMonitorLib 0.9.6`，但依赖只存在于
`PerfMonitor.ProviderWorker`。Agent 通过固定同目录子进程和 1 MiB
长度前缀 JSON 协议读取结果；协议只有 `collect`，不接受命令、脚本、
PowerShell、DLL 路径、Provider 名称或开放插件。

| 指标组 | 周期 | 物理口径 | 无硬件/权限不足 |
|---|---:|---|---|
| gpu | 5 s | 可读 GPU load 最大值、GPU 温度最大值及设备明细 | `not_supported` / `permission_denied` |
| sensors | 5 s | 全部可读温度传感器最大值及明细 | `not_supported` / `partial` |

不同 GPU 和温度读数不相加。Worker 以当前用户 `asInvoker` 运行，部分
传感器需要管理员权限时不会触发 UAC；只局部降低覆盖率。Agent 对 Worker
请求串行化，并设置 10 秒 Provider 超时、滑动窗口重启预算、256 MiB
进程内存上限和进程树终止。设备原始硬件标识不出 Worker；实时设备 ID 只在
一次 Worker 生命周期内稳定，明细不写 SQLite。

## 确定性诊断

首批规则覆盖高 CPU、内存压力、系统盘空间低、配置进程 CPU 尖峰、采样
缺口、Provider 长时间不可用和 Agent 自身资源异常。默认阈值是可修改、
可审计的产品策略，不代表所有硬件的物理极限。

每个事件包含 `ruleId/ruleVersion/severity/state`、激活/恢复滞环、
debounce、cooldown、有界证据窗、`firstSeen/lastSeen` 和 confidence。
仅在完整 debounce 条件成立后产生 `active` 或 `resolved` 转换；阈值间
区域保持当前状态，不把抖动误判为反复转换。

| 数据 | 层级 | 默认位置/上限 |
|---|---|---|
| 规则策略 | 持久化配置 | `diagnostic-policy-v1.json` |
| debounce/cooldown/活动状态 | RAM | 每规则/主体一个有限状态机 |
| 最近事件 | RAM | 最多 2,000 条 |
| 历史事件 | SQLite schema v2 | 最多查询 2,000 条、366 天 |

策略文件损坏或不可写时只把诊断策略降级为 RAM 默认值并输出稳定警告码，
不停止 Provider、IPC 或实时快照。配置进程规则默认 watchlist 为空，策略
只保存进程名，不接受路径、命令行或脚本文本。

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
- `getHealth`
- `queryHistory`
- `queryDiagnostics`
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

schema v2 复用同一个 writer channel 写诊断事件，不创建第二个 SQLite
writer。v1 预留事件表会保存在 `diagnostic_events_v1` 归档表中；迁移不
静默删除旧数据库。

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
v0.6 另覆盖七类规则、滞环/debounce/cooldown、SQLite 事件去重、IPC
诊断查询，以及从 raw snapshot 历史重放得到字节等价事件。
v0.7.0 另覆盖网络单调差分、网卡计数器复位、PDH 首样本、电池不存在/
未知哨兵值和状态型 Provider 释放。
v0.7.1 另覆盖 Worker 协议边界、BOM/超大/损坏帧、非物理读数、崩溃/
挂起回收、重启预算、内存超限、共享采集以及真实发布 Worker 握手。

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
dist/agent/provider-worker/perf-monitor-provider-worker.exe
dist/agent/provider-worker/THIRD-PARTY-NOTICES.md
dist/desktop/perf-monitor-desktop.exe
dist/build-manifest.json
```

manifest 记录全部 Agent/Worker/Desktop 文件的 SHA-256 与大小、Git commit/dirty
状态、所有 Python/.NET 锁文件哈希、.NET SDK、Python oracle 版本和构建
环境。当前产物为 .NET 10 framework-dependent `win-x64`；1.0 的安装包、代码签名、
SBOM 和来源证明另按发布门禁实现。

`scripts/smoke.ps1` 会启动真实 Agent、SQLite 和 Desktop，关闭 Desktop 后
确认 Agent 序列继续增长，再等待 Agent 正常退出并校验数据库。

## 模块边界

```text
src/PerfMonitor.Contracts/          contract v1 DTO、稳定 ID 与历史上限
src/PerfMonitor.Core/               Provider 契约、调度、原子快照与 fan-out
src/PerfMonitor.Collectors.Windows/ 非提权 Windows/PDH Provider
src/PerfMonitor.Collectors.Worker/  Worker 客户端、进程/资源与重启隔离
src/PerfMonitor.ProviderWorker.Protocol/ 固定有界内部协议
src/PerfMonitor.ProviderWorker/     GPU/温度硬件库唯一加载进程
src/PerfMonitor.Ipc.NamedPipes/     安全 framing、ACL、Server/Client 与订阅
src/PerfMonitor.Storage.Sqlite/     migration、单写者、retention 与 rollup
src/PerfMonitor.Agent/              产品 Agent 生命周期与查询适配
src/PerfMonitor.Desktop/            独立 WPF 客户端、重连与最新状态保留
src/PerfMonitor.Diagnostics/        确定性规则、策略、状态机与历史重放
perf_monitor/                       只读 Python 指标 oracle
contracts/v1/                       Schema、目录、IPC 文档与 fixtures
scripts/compare_agents.ps1          Python/.NET 同窗差分
scripts/run_agent_soak.ps1          实际资源与 72 小时发布门禁
```

## 回退与已知风险

- v0.7.1 不改变 SQLite schema，可直接回退到冻结的 v0.7.0 候选。回退到
  v0.5.0 前必须恢复 v0.6 migration 前的数据库备份或使用独立数据目录，
  因为 v0.5 不理解 schema v2。更早的 v0.4 不读取 SQLite。任何回退不得
  删除 `%LOCALAPPDATA%\PerfMonitor\data`，以便重新升级后恢复历史。
- migration 或写入失败会降级历史能力，但不会停止实时采样。状态目前通过
  稳定错误码暴露；v0.6 的 RAM 诊断仍可查询，但该期间事件可能无法持久化。
- Named Pipe DACL 的结构在 CI 中验证；跨用户实机矩阵仍需在 1.0 安装/
  升级验证环境复测。
- PDH `PhysicalDisk(_Total)` 在计数器损坏或被系统禁用时会局部降级
  `diskIo`，不会回退到 WMI、PowerShell 或假值；修复系统计数器后自动恢复。
- 厂商 GPU/温度 SDK 可能阻塞、崩溃或需要更高权限；独立 Worker 超时后
  会被终止，只有 `gpu`/`sensors` 降级。产品不会为补齐传感器自动提权。
