# 2026-10-02 后台驻留与开发基线

**状态：实现、统一构建/测试及两组正式后台短样本通过；仍为 1.1.0 开发候选。**
本轮是 `1.1.0` 开发候选在基线提交
`348a6091c5eb10b55baa87850263df0eae306634` 上的增量，没有生产发布结论。
升号前验收、上一轮检查点和原产物证据保持原样。

## 本轮范围与当前结果

- Desktop 增加正常产品启动选项 `--start-hidden`：复用既有托盘、消息
  HWND、热键、Agent 会话与显示/隐藏入口，启动时不显示或激活窗口；
  托盘不可用时以 `desktop_start_hidden_tray_unavailable`/退出码 3 拒绝
  后台驻留，未知或重复参数以 `desktop_invalid_arguments`/退出码 2
  拒绝。无参数默认启动保持原行为。
- 中间 Desktop Lifecycle 定向测试为 15/15，新增四个隐藏启动/参数用例。
  原始记录为
  `artifacts/development-348a609-desktop/validation/desktop-lifecycle-tests.log`。
  此中间结果不替代最终统一构建、全量测试或物理操作验收。
- 新开发预览入口 `scripts/start_desktop_preview.ps1` 默认只检查输入。
  后台启动需显式 `-StartHidden` 与有限时长；入口绑定显式隔离产物路径、
  当前 HEAD 与版本资源，保留启动进程对象，清理失败报告失败。
  PE 中的 HEAD 后缀仅证明提交元数据匹配，不能证明当前 dirty 源码与产物
  一一对应；本轮另记录实际源码与输入/payload 哈希、构建命令及证据边界。
- `scripts/run_product_baseline.ps1` 复用既有资源探针口径、发布哈希工具
  和预览的进程/流/隔离数据做法；`scripts/read_baseline_history.py` 在
  产品退出后以 SQLite 只读模式检查持久化覆盖及质量元数据，不输出
  进程名、设备明细、路径或诊断对象。
- Python 固定门禁的预期版本同步为当前 `1.1.0`，并继续核对发布工具
  约束；后续升号仍须同步此明确预期。冻结所有脚本后 CPython 3.12.10 全量
  `76/76` 通过，`pip check` 无冲突。
- 最终统一目录 `artifacts/development-348a609` 已完成 locked restore
  21 项目、Release .NET 测试 `208/208`（Contract 11、Actions 33、
  Broker 27、Agent 127、Release 10，零失败/跳过）、solution build
  21 项目零警告零错误。当前数值仅限本文两组短时开发样本，不构成优化
  收益结论。

隐藏启动及物理待验边界详见
[Desktop 交互计划](2026-10-02-desktop-interaction-plan.md)。本轮没有用
外部 ShowWindow、UI Automation 或输入注入制造窗口状态；隐藏模式只
传递正式产品参数，观测器仅只读检查本轮所属窗口的可见/最小化状态。

## 可复现测量方式

以下路径和命令是本轮冻结的 dirty `348a609` 工作树执行记录。
`artifacts/development-348a609` 不表示后来提交的二进制已经重建；提交后
HEAD 改变，预览入口会按设计拒绝其旧提交后缀。日常复现应先对当前 HEAD
locked restore/Release build 到新的独立目录，再做 CheckOnly；不得把这些
历史路径直接用作当前提交的预览入口。

先用最终统一输出做 `-CheckOnly`，再串行执行 60 秒工具校验。主 Agent
确认其输出/清理正确后，后台首轮每模式测量 180 秒、启动暖机 10 秒、
探针周期 1 秒。禁止与构建、全量测试或其他测量并行；当前用户工作负载
没有被控制或暂停。

```powershell
$build = 'D:\GitHub\windows-performance-monitor\artifacts\development-348a609'
$agent = Join-Path $build 'bin\PerfMonitor.Agent\release_win-x64\perf-monitor-agent.exe'
$desktop = Join-Path $build 'bin\PerfMonitor.Desktop\release_win-x64\perf-monitor-desktop.exe'
$worker = Join-Path $build 'bin\PerfMonitor.ProviderWorker\release_win-x64\perf-monitor-provider-worker.exe'
$python = 'C:\Users\Administrator\AppData\Local\PerfMonitor\venv-3.12.10\Scripts\python.exe'
$runtime = 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302'
.\scripts\run_product_baseline.ps1 -AgentPath $agent -DesktopPath $desktop `
  -WorkerPath $worker -PythonPath $python -DotnetRoot $runtime `
  -Mode agent-only -HardwareProfile workerDeployed -CheckOnly
.\scripts\run_product_baseline.ps1 -AgentPath $agent -WorkerPath $worker `
  -PythonPath $python -DotnetRoot $runtime -Mode agent-only `
  -HardwareProfile workerDeployed -DurationSeconds 60 `
  -OutputDirectory 'artifacts/baseline/2026-10-02-wave02-agent-toolcheck-60s-v2'
.\scripts\run_product_baseline.ps1 -AgentPath $agent -WorkerPath $worker `
  -PythonPath $python -DotnetRoot $runtime -Mode agent-only `
  -HardwareProfile workerDeployed -DurationSeconds 180 `
  -OutputDirectory 'artifacts/baseline/2026-10-02-wave02-agent-worker-180s'
.\scripts\run_product_baseline.ps1 -AgentPath $agent -DesktopPath $desktop `
  -WorkerPath $worker -PythonPath $python -DotnetRoot $runtime `
  -Mode desktop-hidden -HardwareProfile workerDeployed -DurationSeconds 180 `
  -OutputDirectory 'artifacts/baseline/2026-10-02-wave02-agent-worker-hidden-180s'
```

`agent-only` 指没有 Desktop，集合仍包含 Agent 与发现的所属 Worker；
不能把它的结果称为只测一个 Agent 进程。`workerDeployed` 使用标准固定
Worker 部署路径，复制实际最终构建输出，不移除组件。可选
`hardwareUnavailableDevelopmentProfile` 只接受源部署本来没有 Worker
且未提供 WorkerPath 的明确开发样本；硬件 Provider 仍注册，无法采集时
表现为 unavailable，它不是产品正式禁用功能或标准部署基线。

每次运行创建唯一 `artifacts/baseline/<日期时间>-wp00-<模式>-<随机ID>`
目录，已有输出路径直接拒绝覆写。复制输入到本轮 payload，使用新 data
目录，子进程单独设置 `DOTNET_ROOT`/`DOTNET_ROOT_X64`，不修改系统 PATH
或全局环境。预检查现存产品进程和当前用户 pipe，不接管其他实例。
Agent 使用默认采集/存储/诊断、quiet、无文件快照输出、1 秒投递及有限
运行时长；新数据目录的默认动作策略禁用动作、dry-run-only，不启动
Broker 或服务。结束时 Agent 正常排空退出，Desktop 在测量完成后仅由
保留的 owned Process 对象终止；此方式不是物理托盘退出验收。

## 记录口径与证据边界

- `process-resources.csv` 逐进程记录 PID＋启动时间、累计 CPU 时间、
  private/working set 字节、句柄和线程。Worker 登记前核对 CIM 创建时间
  与持有句柄的实际创建时间/映像路径，防止 PID 复用误认；清理仅作用
  于保留且验证归属的对象，清理失败不能报告完整成功。
- `product-resources.csv` 汇总实际观测进程集合。CPU 为
  `100 × ΣΔCPUTime / ΔmonotonicTime` 的单核等效百分比，再除以实际
  逻辑处理器数得到整机归一化百分比；本机目前为 28。未知的首个 CPU
  间隔和不完整读数不当作 0；完整 CPU 覆盖秒数及缺口单独记录。
  计数器读取失败时清空该进程的上一 CPU 基线，恢复后的首个读数只建立
  新基线；Worker 归属确认失败时该探针的集合 CPU 覆盖也标为不完整。
- 两个 CSV 同时记录启动暖机/测量阶段、单调经过时间和 UTC；集合 CSV
  另记录探针迟到、窗口状态与 DPI，摘要记录跳过槽。资源汇总仅使用
  测量阶段；首个跨暖机边界的
  CPU 间隔不进入均值。Polling 可能漏掉短寿命 Worker，首次观察前及退出
  边界 CPU 未完全覆盖时不能把集合称为无缺口的完整全产品统计。
- DB/WAL/SHM 记录实际文件长度；逐探针长度差、暖机后/退出前/退出后
  状态和峰值分别保存。文件长度变化不等于物理写入字节、逻辑写放大、
  稳定日增长或未来保留后的总盘占；working set 相加可能重复共享页。
  JSON 的三个存储状态对象采用 `dbBytes`、`walBytes`、`shmBytes` 明确
  字段，均为字节；可直接用默认 `ConvertFrom-Json` 读取。
- `baseline.json` 记录参数、OS build/架构、CPU/内存/供电、源码 HEAD＋
  dirty 状态及源码文件哈希、输入和实际 payload 哈希、进程生命周期、
  清理结果及汇总。只读历史检查给出持久化 delivery 缺口、经过时间间隔
  和分组质量次数；它只覆盖落盘序列，含有限退出余量，不证明未持久化的
  瞬时 Provider/fanout 观测完整。
- 原始数据库、二进制、stdout/stderr 和其他应用/进程隐私数据留在本地。
  待测量后只归档必要摘要、哈希、测试日志/TRX及经过检查的资源 CSV；
  归档文件按既有精确 `.gitattributes` 规则保留原件字节。

## Worker 启动链核查

本仓库 `LibreHardwareMonitorSnapshotSource` 启用 CPU/GPU/主板/存储并
调用 `Computer.Open`。本地 NuGet `LibreHardwareMonitorLib 0.9.6`
metadata 固定上游提交 `3d331e3370efb858411f19511373eff65a218701`。
该精确源码的
[Computer.Open](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/Hardware/Computer.cs)
进入 SMBios/Mutexes/OpCode 与分组，
[PawnIo.LoadModuleFromResource](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/3d331e3370efb858411f19511373eff65a218701/LibreHardwareMonitorLib/PawnIo/PawnIo.cs)
用 `OPEN_EXISTING` 打开既有 PawnIO 设备，随后可用 IOCTL 加载模块；这两段
已核对启动链没有驱动/Windows 服务安装或启动调用。

本机本次只读查询未发现 PawnIO 卸载项或同名系统驱动实例。主 Agent 据此
批准按实际 Worker 标准路径测量，缺失底层能力按真实 degraded/unavailable
记录；不安装驱动、服务或更改权限。该核查不是对所有厂商库及传递依赖
的逐行审计，也不声称硬件覆盖完整。

## 实际后台测量与待测项

首轮工具校验原件保留在
`artifacts/baseline/2026-10-02-wave02-agent-toolcheck-60s`，标为无效工具
自检，不能用于正式 CPU 数值结论。它虽报告 `completed=true` 和驱动退出
码 0，但存储哈希表使用空文件后缀作为 JSON 属性名，默认
`ConvertFrom-Json` 无法解析；此外 PowerShell 为 `Math.Max(0, double)`
选用了整数重载，把小于半秒的 CPU 增量舍入为 0。原始累计 CPU 有增长，
原 CPU 增量与均值全为 0，证明测量器的数值链错误。

修复使用明确存储字段与 double 运算，并补强读取失败、Worker 归属确认
失败时的覆盖标志。实际脚本 AST 的 17 项纯数值/文件夹回归已分别在
PowerShell 7.6.5 与 Windows PowerShell 5.1.19041.6456 通过，覆盖分数
CPU、累计差、集合求和、时间加权均值、整机归一化、暖机跨界、读取失败
恢复和默认 JSON 解析。记录为
`artifacts/development-348a609/validation/baseline-numeric-selfcheck-ps7.json`
和 `baseline-numeric-selfcheck-ps51.json`；同目录的
`baseline-numeric-selfcheck.ps1` 可复现这些离线检查。离线重算只用于证明
缺陷与回归，不替代新一轮实际测量。有效区间数以实际过滤后的结果为准，
不以请求时长或探针行数代替。

该原件的 Worker 退出码 `-1` 是实际操作系统读数。源码关闭链
`AgentServiceRunner` → `ProviderScheduler` → `HardwareWorkerCoordinator`
→ `HardwareWorkerClient.ResetSessionAsync` → `ProcessWorkerSession.Abort`
会在 Agent 有限时长结束后主动 `Kill(entireProcessTree: true)` 回收所属
Worker；本轮结束状态符合该路径。`cleanup='already exited'` 只表示
wrapper 清理时进程已退出，不等于 Worker 自然成功结束，亦不构成今后
无条件忽略非零退出的规则。探针期间的提前产品退出仍使测量失败。

第二次 `-60s-v2` 工具校验默认 JSON 解析成功，独立 CSV 重算与摘要一致：
70 个总探针、60 个测量点、59 个有效 CPU 区间，覆盖 59.0073269 秒、
CPU 增量 4.265625 秒。核心等效时间加权均值 7.22897515%，28 逻辑核
归一化均值 0.258177684%；这些数值只用于工具验证。输入/源码/payload
哈希均一致，只读历史检查为 76 个持久化快照、0 个区间内 delivery 缺口，
integrity `ok`。Agent 退出 0，Worker 收尾退出 -1，无采样/归属确认/清理
错误及遗留产品进程。主 Agent 据此放行两组 180 秒开发样本。

两组正式样本均为 180 秒请求、10 秒暖机、1 秒探针，每组 190 总探针、
180 测量点、179 有效 CPU interval。下表 CPU 是有效区间的时间加权均值；
私有内存 P95 是 180 个测量点按 nearest-rank `ceil(0.95*180)=171`
取值，峰值单独列出，单位 MiB（1,048,576 字节）。

| 正式后台样本 | CPU 核心等效 / 28 核归一化 | CPU 覆盖秒 / 请求覆盖比例 | 私有内存 P95 / 峰值 MiB | 持久化快照 / 区间内 delivery 缺口 |
| --- | --- | --- | --- | --- |
| Agent＋所属 Worker | 6.842852% / 0.244388% | 179.018932 / 99.454962% | 98.426 / 107.844 | 194 / 0 |
| Agent＋所属 Worker＋隐藏 Desktop | 7.838493% / 0.279946% | 179.004439 / 99.446910% | 199.664 / 206.602 | 194 / 0 |

| 角色 | 无 Desktop：私有内存 P95 / 峰值 MiB | 隐藏 Desktop：私有内存 P95 / 峰值 MiB |
| --- | --- | --- |
| Agent | 63.152 / 72.094 | 72.313 / 79.352 |
| Worker | 38.406 / 38.965 | 38.156 / 38.777 |
| Desktop | 未运行 | 91.523 / 95.270 |

两组原始 CSV→摘要、源码/输入/payload 哈希及只读历史复算均一致。
分组质量包含真实 warming-up/partial，未伪造全硬件覆盖；历史完整性为
`ok`，invalid 时间/delivery 元数据、Worker 归属错误、探针跳槽/大间隔
均为 0。隐藏组所有 190 个观测点均无可见 Desktop 窗口，正式参数为
`--start-hidden`；轮询不证明每个瞬间均无闪窗，程序级无闪窗另由定向
测试覆盖。Agent 均退出 0；Desktop 由 wrapper 在测量后终止，Worker
由有限 Agent 收尾回收，两者 OS 退出码 -1，清理后产品残留为 0。

两组为串行、未控制其他用户负载的短样本，不构成配对因果实验。
集合差值不能解释为 Desktop 独立增量、统计显著性或优化收益；逐角色
读数仅提供后续定位输入。持久化统计含结束余量，也不等于精确测量窗口
中的全部瞬时投递。

原件目录分别为 `artifacts/baseline/2026-10-02-wave02-agent-worker-180s`
及 `.../2026-10-02-wave02-agent-worker-hidden-180s`。
[证据索引](evidence/2026-10-02-background-baseline/README.md)、
[机器可读摘要](evidence/2026-10-02-background-baseline/validation-summary.json) 与
[精确来源及哈希](evidence/2026-10-02-background-baseline/artifact-hashes.json)
将工具失败、工具通过与两正式样本分开记录。

Desktop 可见模式、物理托盘/热键/焦点竞争、多 DPI/多屏、100/500/1000
真实进程规模、场景任务对照、真实 72 小时、完整支持矩阵与动作恢复仍
未验。本机 Windows 10 仅是开发环境；这两个后台短样本也不足
以判定稳态预算、前台增量或全产品性能基线全部完成。下一波 WP01/WP04
应以实测路径和质量缺口为输入，不先承诺优化收益。

交接：下步 WP01/WP04 先依据上述逐角色读数与真实质量缺口选择要复现、
测量的路径，再决定是否优化。本轮不改变版本、不发布或 tag；下一提交
应重新构建当前 HEAD 的独立预览产物，不追改这里的冻结 dirty 348 证据。
