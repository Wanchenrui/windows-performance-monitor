# 电脑性能监控

当前版本为 `0.4.0`：在冻结的 contract v1 上新增 `net10.0-windows`
非提权 Agent，并以 Python 0.3 作为只读指标口径 oracle 做旁路差分。Python
路径不再增加指标；Desktop、Named Pipe、SQLite、诊断和 Broker 仍不属于
本版本。

## 0.4 的工程边界

- `.NET 10` Agent 直接使用 Windows API 采集 CPU、内存、卷容量、uptime
  和进程指标，并采集自身 CPU、内存与 GC heap。
- CPU、内存、uptime、自监控按 1 秒，进程按 2 秒，卷容量按 30 秒独立
  调度。每个 Provider 使用绝对期限、超时、同 Provider 不重入、有界并发
  和失败退避；单个 Provider 失败只降级对应指标组。
- `SnapshotAssembler` 每次发布完整不可变视图，availability、freshness 和
  coverage 保持正交；进程差分键仍为 `(PID, creationTimeTicks)`。
- Python 0.3 HTTP/UI 只保留为 oracle、golden fixture 生成器和回退基线，
  不再承担后续产品能力扩建。
- GPU、网络、温度、告警、SQLite、正式 IPC、Desktop 和优化动作不在本
  版本范围内。

## 首次运行

运行要求：Windows 10/11、64 位 Python 3.12。契约跨语言测试另需 .NET 10 SDK。

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\setup.ps1
.\启动.bat
```

启动脚本优先使用项目内 `.venv`。浏览器只由 `app.py` 打开一次；当前用户
会话再次启动时（即使请求了不同端口）会通过命名互斥体、当前用户瞬态实例
文件和随机 `instanceId` 识别并打开现有页面。若目标端口被其他程序占用，
程序不会仅凭对方返回的服务字符串判断为已有实例，而会显示明确错误并以
退出码 `2` 结束。

无托盘运行：

```powershell
.\.venv\Scripts\python.exe .\app.py --no-tray
```

可用参数：

| 参数 | 环境变量 | 默认值 | 说明 |
|---|---|---:|---|
| `--port` | `PERF_MONITOR_PORT` | `7700` | 本地 HTTP 端口 |
| `--sample-interval` | `PERF_MONITOR_SAMPLE_INTERVAL` | `1` 秒 | 允许 0.25～60 秒 |
| `--history-minutes` | `PERF_MONITOR_HISTORY_MINUTES` | `60` 分钟 | 允许 1～1440 分钟 |
| `--no-browser` | — | 关闭 | 不自动打开浏览器 |
| `--no-tray` | — | 关闭 | 控制台模式运行 |
| `--log-level` | `PERF_MONITOR_LOG_LEVEL` | `INFO` | 日志级别 |

日志位于 `%LOCALAPPDATA%\PerfMonitor\logs\perf-monitor.log`，单文件最大
1 MiB，保留 3 个轮转文件。

## 指标口径

系统 CPU 为 Windows 系统 CPU 时间得到的 0～100% 整机负载。

进程 CPU 同时提供两个视图。整机归一化值为：

\[
U_p=100\%\times
\frac{\Delta CPUTime_p}{\Delta t\times N_{\mathrm{logical}}}
\]

`cpuNormalizedPct` 被限制在 0～100%，用于默认排行；`cpuCoreEquivalentPct`
不除以逻辑处理器数，100% 表示占满一个逻辑处理器，因此多线程进程可以
超过 100%。首次见到的进程没有时间差基线，界面显示“采集中”，不会显示
伪造的 0。

进程 Provider 以普通权限打开受限查询句柄。系统保护进程或瞬间退出的进程
无法读取时会增加 `processCollection.skipped`，并把覆盖状态设为
`limited`；进程排行只代表可访问进程，界面会明确显示覆盖受限，不会申请
管理员权限或假装覆盖完整。

进程内存同时提供：

- `workingSetBytes`：当前驻留物理内存工作集（RSS）。
- `privateBytes`：当前进程私有提交字节数；系统不提供时为 `null`。

系统内存使用量按 `total - available` 计算。容量单位在 API 中统一为字节，
界面换算为 GiB/MiB。系统盘从 Windows 环境和卷挂载信息中识别，不再在
业务代码里固定为 C 盘；API 同时返回所有可访问的固定磁盘。

每个指标组包含来源。不可访问或采集失败时返回 `null`/缺失值和错误详情，
界面显示“不可用”“部分指标不可用”或“数据已陈旧”，绝不把错误伪装成 0。

## 本地接口

- `GET /api/v1/snapshot`：contract v1 原子快照；每组包含 Provider、观测
  时间、availability、freshness、coverage、稳定错误码和带单位指标。
- `GET /api/v1/history?metrics=system.cpu.utilization.percent,...&from=...&to=...&maxPoints=2000`：
  有界历史查询。`from/to` 为 Unix 毫秒时间戳；`maxPoints` 允许
  1～5,000。降采样时间桶保留 `min/max/avg/last`。
- `GET /api/v1/capabilities`：Provider、指标、稳定错误码、端点和限制。
- `GET /api/v1/health` 与 `GET /api/health`：contract v1 轻量健康响应。
- `GET /api/stats`：旧 `apiVersion=0.2.1` 的 deprecated 有界适配器；新
  客户端不得依赖。

.NET Agent 的产品版本为 `0.4.0`，只读 Python oracle 保持 `0.3.0`；
两者公开 `contractVersion` 均独立固定为 `1.0`。Schema、ID 目录、兼容
规则、IPC framing 与 golden fixtures 位于 `contracts/v1/`。

## 测试

```powershell
.\.venv\Scripts\python.exe -m pytest
```

测试覆盖进程 CPU 公式、指标单位与来源、历史时间/绝对点数双重裁剪、
服务端降采样、质量语义、实例防伪、JSON Schema、golden fixture 再生成、
所有公开 JSON 响应和未知新增字段兼容性。

.NET 10 客户端 DTO 必须反序列化全部 Python fixtures：

```powershell
dotnet test .\PerfMonitor.slnx --configuration Release
```

发布并获取一次 .NET Agent 快照：

```powershell
dotnet publish .\src\PerfMonitor.Agent\PerfMonitor.Agent.csproj `
  --configuration Release --output .\artifacts\agent
.\artifacts\agent\perf-monitor-agent.exe `
  --once --quiet --warmup-seconds 3 `
  --output .\artifacts\agent-snapshot.json
```

Python/.NET 差分门禁按同窗均值比较 CPU 与内存，并精确检查单位、source
ID、卷总容量、空值语义和进程身份：

```powershell
.\scripts\compare_agents.ps1 `
  -AgentPath .\artifacts\agent\perf-monitor-agent.exe `
  -Samples 20 `
  -OutputPath .\artifacts\agent-differential.json
```

CPU/内存均值绝对差默认不得超过 3/1 个百分点；P95 会记录为证据，但不把
相位敏感的瞬时峰值当作相等门禁。

真实 72 小时发布长稳使用同一个可执行脚本，默认时长为 259,200 秒：

```powershell
.\scripts\run_agent_soak.ps1 `
  -AgentPath .\artifacts\agent\perf-monitor-agent.exe `
  -OutputPath .\artifacts\agent-soak-72h.json
```

CI 另运行短时真实进程资源门禁，并用单元测试快速推进 259,200 个绝对期限
和 259,200 次快照替换。短时/虚拟结果不能替代
`release72HourGate.actualWallClockPassed=true` 的真实墙钟证据。

在目标 Windows 机器上执行 10 秒参考计数器对照：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\compare_reference.ps1
```

脚本比较系统 CPU 十秒均值与 `Processor(_Total)\% Processor Time`，并用
`Memory\Available MBytes` 按相同物理内存口径计算内存占用；CPU/内存默认
偏差门限分别为 ±3 和 ±1 个百分点。该测试只在验证期间使用性能计数器，
不会把 PowerShell 或性能计数器引入产品采样路径。

## 可复现构建

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

构建脚本会先检查锁定依赖并运行全部测试，随后使用固定版本的 PyInstaller
生成 `dist\perf-monitor.exe`，验证版本资源，并输出带 SHA-256、Git
commit/dirty 状态、依赖锁文件哈希、完整 Python 版本与架构、PyInstaller
版本和构建环境标识的 `dist\build-manifest.json`。随后可执行
`scripts\smoke.ps1` 验证 EXE 启动、健康接口和正常退出。这里的“可复现”
指固定源代码、Python 3.12 和锁定依赖可重复生成可运行产物，不承诺二进制
逐字节相同。

`version_info.txt` 中的发布者目前是明确的占位值。商业发布前必须替换为
法定主体名称，并使用该主体的代码签名证书签署最终 EXE/安装包；本仓库
没有签名私钥，也不会在构建脚本中绕过签名要求。

## 模块边界

```text
app.py                              启动、单实例、托盘、生命周期
perf_monitor/collector.py           Python 指标语义参考 Provider
perf_monitor/contract_v1.py         旧采集结果到 contract v1 的适配器
perf_monitor/errors.py              跨语言稳定错误码映射
perf_monitor/sampling.py            固定周期与显式时间语义
perf_monitor/store.py               原子快照与有界 RAM 历史
perf_monitor/server.py              contract v1 HTTP/兼容适配器
contracts/v1/                       Schema、ID 目录、IPC 与 fixtures
src/PerfMonitor.Contracts/          net10.0 DTO
src/PerfMonitor.Core/               Provider 契约、调度器与原子快照
src/PerfMonitor.Collectors.Windows/ 非提权 Windows Provider
src/PerfMonitor.Agent/              net10.0 Agent 生命周期与 JSONL 输出
static/                             contract v1 浏览器客户端
tests/                              Python/.NET 契约、故障与长稳门禁
scripts/compare_agents.ps1          Python/.NET 同窗差分
scripts/run_agent_soak.ps1          真实进程资源与 72 小时发布门禁
```

该拆分是原型迁移边界，不代表最终商业架构。后续原生代理、SQLite、IPC、
诊断引擎和提权 Broker 应独立演进，不能重新合并进 `app.py`。

## 回退与已知风险

- `v0.3.0` 是冻结契约和 Python oracle 标签；0.4.0 可直接回退到该标签，
  不涉及数据库或安装格式迁移。
- 低于 1 秒的采样会增加进程枚举开销，只用于测试，不建议作为默认配置。
- 传感器、GPU 和温度尚未实现；界面不会用 0 代替这些缺失能力。
