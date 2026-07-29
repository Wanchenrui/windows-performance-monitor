# 电脑性能监控

当前版本为 `0.2.1`：一个完成基线封口的 Windows 10/11 本地性能监控
参考实现。它仍不是商业成品，后续功能将在独立的 .NET Agent 架构中演进。

## 0.2 的工程边界

- 系统与进程指标由 `psutil` 的 Windows 原生后端采集，不再周期性启动
  PowerShell/WMI 子进程。
- 默认按 1 秒固定周期采样，调度期限为
  \(t_k=t_0+kT\)。采集超时会跳过已经过期的周期并记录缺口，不累积漂移。
- RAM 中的历史记录按真实时间裁剪，默认保留最近 60 分钟，并有 86,400
  点绝对上限。历史接口单次最多返回 5,000 个时间桶。
- HTTP 接口只监听 `127.0.0.1`，没有远程访问和遥测上传。
- GPU、网络、温度、告警、SQLite 历史库和优化动作不在本版本范围内。

## 首次运行

要求：Windows 10/11、64 位 Python 3.12。

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

- `GET /api/v1/snapshot`：不含历史的原子实时快照、随机 `instanceId`、
  采样序号、时间、数据年龄、调度抖动、累计缺口和采集错误。
- `GET /api/v1/history?metrics=cpu,mem&from=...&to=...&maxPoints=2000`：
  有界历史查询。`from/to` 为 Unix 毫秒时间戳；`maxPoints` 允许
  1～5,000。发生降采样时，每个时间桶保留 `min/max/avg/last`。
- `GET /api/v1/capabilities`：接口、历史指标、RAM 点数和查询上限。
- `GET /api/v1/health`：实例标识、版本和简要健康状态。
- `GET /api/stats`：0.2 兼容适配器，返回 `Deprecation: true`，历史最多
  2,000 点；新客户端不得继续依赖该接口。
- `GET /api/health`：健康接口的兼容路径。

API 版本为 `0.2.1`。产品版本与正式契约版本将在 0.3.0 起解耦。

## 测试

```powershell
.\.venv\Scripts\python.exe -m pytest
```

测试覆盖进程 CPU 公式、指标单位与来源、历史时间/绝对点数双重裁剪、
服务端降采样统计、`partial + stale`、并发快照一致性、实例伪造防护、
固定周期缺口计算和本地 HTTP 安全响应头。

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
app.py                      启动、单实例、托盘、生命周期
perf_monitor/collector.py   Windows 原生指标 Provider
perf_monitor/sampling.py    固定周期调度
perf_monitor/store.py       原子快照与 RAM 时间窗口
perf_monitor/server.py      版本化本地 HTTP/静态资源
static/                     无外部依赖的浏览器客户端
tests/                      指标、调度、并发和接口测试
```

该拆分是原型迁移边界，不代表最终商业架构。后续原生代理、SQLite、IPC、
诊断引擎和提权 Broker 应独立演进，不能重新合并进 `app.py`。

## 回退与已知风险

- `v0.2.0` 是已提交并打标签的可信化原型基线；0.2.1 可直接按该标签执行
  源码级和产物级回退，不涉及数据迁移。
- 低于 1 秒的采样会增加进程枚举开销，只用于测试，不建议作为默认配置。
- 传感器、GPU 和温度尚未实现；界面不会用 0 代替这些缺失能力。
