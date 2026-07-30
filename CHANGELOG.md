# 变更记录

## 1.0.0

变更点：

- 新增单一 `win-x64`、per-machine WiX 7 MSI：Core 必选，包含非提权
  Agent、Desktop、隔离 Provider Worker 和 Support/Recovery 工具；Broker
  为默认不安装的可选 Feature，显式选择后才创建 LocalSystem 自动服务与
  受保护 ProgramData 目录。
- 新增生产/测试签名模式。Agent、Provider Worker、Desktop、Broker、
  Support 与 MSI 使用 Authenticode SHA-256；生产证书必须具备 Code
  Signing EKU、非自签链、固定主体/指纹和 HTTPS RFC 3161 时间戳。测试
  自签证据永远不具备 production eligibility。
- 新增 detached CMS 签名的 update manifest，并把产品版本、Git commit、
  支持矩阵、MSI/SBOM 哈希与固定证书身份绑定；字节篡改、证书错配和测试/
  生产模式错配均拒绝。
- 新增 CycloneDX 1.6 SBOM、NuGet direct/transitive 与 Python
  `pip-audit` 漏洞门禁、带到期时间和审查信息的显式豁免格式、最终 release
  manifest，以及 GitHub OIDC artifact attestation。
- Python oracle、测试和独立 `pip-audit` 环境的 Windows x64/CPython 3.12
  direct/transitive wheel 全部固定 SHA-256；安装强制
  `--require-hashes --only-binary=:all:`。
- 新增 SQLite migration 前 online backup、backup 哈希/schema/完整性验证、
  受控恢复和更高 schema 启动前拒写；真实 Agent 在 WAL 写入期间被强制
  终止后，必须完整恢复并保持已提交序列。
- 新增 Agent 文件锁单实例边界、Support 数据库校验/备份/恢复、结构化
  allowlist 诊断导出与敏感模式 fail-closed 扫描。
- migration 对任何现有旧 schema 都先备份并在提交后复验；受控恢复绑定
  源文件名、要求 Agent 排他锁/干净 sidecar，并以同目录临时副本原子替换。
- 新增 MSI 表契约和安装、升级、直接降级阻止、卸载数据保留、重装与受控
  回滚/repair 测试；Server Core 被 LaunchCondition 明确阻止。Broker
  另增加真实 Pipe 畸形 framing、截断 JSON、身份/命令
  注入、并发重放、幂等冲突和断连渗透测试；服务模式另把实际客户端绑定到
  Program Files 精确 Agent 路径、SHA-256、WinVerifyTrust Code Signing
  EKU、证书主体及证书 SHA-256 指纹，任一不符均拒绝。
- 冻结 Windows 11 24H2/25H2 与 Windows Server 2022/2025 Desktop
  Experience x64 支持矩阵，以及 CPU、内存、GC、句柄和线程资源预算。
- 新增四台专用 self-hosted runner 的支持矩阵工作流：安装前验证真实
  build/架构/Desktop Experience，四机消费同一 MSI，聚合证据由 GitHub
  attestation 绑定 workflow 与 commit；production 必须按 run ID 取回并
  复验，不能用手工 JSON 绕过。
- 真实 72 小时基线新增冻结候选描述：固定 v0.4 源 head、CI run、artifact
  digest、Agent 与验证器 SHA-256。生产发布会重新按版本化预算计算资源
  结果，并要求证据中的 Agent hash、产品版本和真实 UTC 跨度同时匹配，
  不再仅信任可替换的 `passed` 布尔值。
- Broker 监听器对客户端在 `ConnectNamedPipe` 完成前立即断开的 Windows
  `ERROR_NO_DATA (232)` 仅丢弃该 Pipe 实例并继续监听；其他非停止期 I/O
  故障仍上抛。新增确定性回归测试覆盖该畸形客户端时序。

潜在风险：

- WiX 7 使用 OSMF EULA v1.1，MSI 构建必须由已审阅许可的操作者在每次
  本地/CI 调用中显式传入 `wix7`；仓库不默认接受。
- GitHub Windows runner 只能证明一个 Server x64 环境，不能替代所有
  Windows 11/Server 目标的独立实机安装证据。
- 正式生产候选还依赖外部受信代码签名证书、时间戳服务、真实 72 小时墙钟
  证据及精确 GitHub run/attestation；任一缺失时只能称为 test candidate。
- 卸载默认保留用户历史和 Broker 审计，避免事故证据丢失；这意味着彻底
  删除数据必须是独立、明确的用户/管理员操作。

回退：

- 保存脱敏诊断证据并验证上一份 MSI 的签名/哈希；停止 Agent 与 Broker，
  卸载 1.0 后安装上一份候选。数据库保持原位。只有存在 schema migration、
  backup 哈希与 schema 均匹配且管理员确认接受丢失升级后数据时，才执行
  Support 工具的受控 restore。

## 0.7.2

变更点：

- 新增独立、可选的 `PerfMonitor.Broker` Windows 服务边界；服务模式只允许
  LocalSystem 和固定 ProgramData/Named Pipe，console 模式始终强制
  dry-run，Broker 缺失不会延迟或停止 Agent 采样。
- action contract 仅包含进程优先级、终止同用户进程、固定 Broker
  self-check 和三个编译期电源 profile ID；协议拒绝未知字段以及 caller
  身份、路径、命令、参数、环境、脚本、注册表和任意 GUID。
- Agent 用户策略与 Broker 机器策略默认关闭全部动作；Broker 从 Pipe
  transport 解析实际 SID/PID/映像/SHA-256，并在进程动作临界区重验
  `(PID, creationTimeTicks, owner SID)`、保护进程和允许枚举。
- 新增 Broker 独立 SQLite 审计库：OS 动作前提交 `pending`，以
  `(caller SID, idempotencyKey)` 实现持久化 at-most-once；同摘要重取
  已存结果，冲突拒绝，崩溃遗留状态恢复为 `indeterminate` 且永不自动重放。
- 发布产物新增独立 `dist/broker`，构建 manifest 纳入其哈希与锁文件。
  CI/本地测试覆盖闭合 framing、真实 Pipe 身份解析、PID 复用/owner/保护
  目标、四类 fake executor、审计故障/恢复，以及真实发布 Broker 的强制
  dry-run 与幂等重取。

潜在风险：

- v0.7.2 冻结服务兼容二进制和安全协议，但服务安装、ProgramData ACL、
  Authenticode 客户端发布者验证及升级切换仍属于 v1.0 安装门禁；候选版本
  不应手工启用真实动作到生产环境。
- OS 修改与 SQLite 不能形成同一原子事务。修改后、终态提交前崩溃只能
  报告 `indeterminate`；调用方必须人工核对，不能把超时当作失败并重试。
- 机器级电源动作影响整机而非单用户，因此除 SID/映像校验外还必须由机器
  策略逐项启用；默认策略保持关闭。

回退：

- v0.7.2 不修改用户历史 schema。先停止并卸载 Broker 服务，再回退
  Agent/Desktop 到 v0.7.1；保留
  `%ProgramData%\PerfMonitor\broker\broker-v1.db` 作为安全审计，不交给
  旧版本写入。未安装 Broker 的部署可直接回退二进制。
## 0.7.1

变更点：

- 新增当前用户 `asInvoker` 的独立 Provider Worker，固定采集 GPU load、
  GPU/CPU/主板/存储温度；`LibreHardwareMonitorLib 0.9.6` 不进入 Agent、
  Core 或稳定 Windows Collector 的依赖闭包。
- 新增内部协议 v1：4 字节小端长度前缀、1 MiB 上限、固定 `collect`、
  request/instance/sequence 校验，以及设备、名称、传感器与物理范围上限。
- Agent 以固定同目录路径、`UseShellExecute=false` 和无 BOM UTF-8 管道
  启动 Worker；请求串行，GPU/温度 Provider 在 250 ms 内复用一次硬件
  快照。
- Worker 超时、崩溃、协议损坏、进程内存超限或重启循环只使 `gpu`/
  `sensors` 局部降级；进程树终止、Job Object 和滑动窗口重启预算限制
  资源影响。
- contract v1 新增 GPU/温度稳定 ID 与 `celsius` 单位；历史只保存最大
  load/温度聚合，不持久化设备名、底层标识或逐传感器明细。
- Desktop 增加 GPU 和硬件最高温度卡片；发布产物包含独立 Worker、
  锁文件哈希和第三方许可说明，并执行真实协议握手。

潜在风险：

- LibreHardwareMonitor 官方说明部分传感器需要管理员权限。产品不自动
  提权，因此某些机器会明确显示 `partial`、`permission_denied` 或
  `not_supported`，而不是伪造为 0。
- GPU load 定义为驱动公开的 load 传感器集合最大值，不宣称等于任一
  厂商专有的“核心利用率”。设备 ID 只在单次 Worker 生命周期内稳定。
- Job Object 无法嵌套时仍有私有内存检查和整棵进程树终止，但硬内存限额
  由操作系统是否接受 Job 绑定决定。

回退：

- v0.7.1 没有 SQLite migration，可保留数据目录并回退到 v0.7.0；旧
  二进制忽略新增 metric ID。单独移除 `provider-worker` 目录也只会让
  两个硬件组降级为 `not_supported`。

## 0.7.0

变更点：

- 新增 1 秒网络吞吐 Provider，以活动非回环网卡累计收发字节和单调时钟
  计算速率；基线按接口 ID 保存在 RAM，新接口和计数器复位不产生假尖峰。
- 新增 1 秒磁盘 I/O Provider，使用语言无关的
  `PdhAddEnglishCounter` 读取 `PhysicalDisk(_Total)` 读写字节/操作速率；
  首样本按 PDH 双样本物理约束保持 `null`。
- 新增 5 秒电源/电池 Provider，使用 `GetSystemPowerStatus` 表达交流/
  电池供电、电池存在、充电、节能、电量和寿命；255/`UINT_MAX` 哨兵值不
  映射为 0。
- contract v1 追加稳定 group/provider/source/metric ID，以及
  `byte_per_second`、`count_per_second` 单位；网络、磁盘和电量进入有界
  SQLite 历史白名单。
- Desktop 增加网络、磁盘和电源只读状态；Provider 调度器在所有采集任务
  停止后释放 PDH 原生查询句柄。

潜在风险：

- 网络汇总包含活动虚拟接口，可能同时计算隧道与底层适配器；该值定义为
  “网卡计数器总吞吐”，不宣称等于去重后的物理链路流量。
- `PhysicalDisk(_Total)` 被禁用或系统性能计数器损坏时，只有 `diskIo`
  局部不可用；不会用 WMI/PowerShell 或 0 值兜底。
- 本阶段不包含 GPU、温度、厂商 SDK 或系统修改动作，分别由后续隔离
  Worker 和 Broker 版本实现。

回退：

- v0.7.0 没有 SQLite migration，可回退到 v0.6.0 候选；旧二进制会忽略
  新指标行。保留数据目录，不需要删除历史数据库。

## 0.6.0

变更点：

- 新增确定性诊断模块，首批实现高 CPU、内存压力、系统盘空间低、配置
  进程 CPU 尖峰、采样缺口、Provider 长时间不可用和 Agent 自身资源异常。
- 每个事件固定携带规则/版本、严重度、状态、滞环、激活/恢复 debounce、
  cooldown、有界证据窗、首次/末次时间和 confidence；转换 ID 由确定性
  输入计算，同一 raw snapshot 历史重放产生字节等价事件。
- 诊断使用单线程有界队列和快照逻辑时间，不读取 evaluator 墙钟；队列满、
  策略损坏或事件 sink 失败均不能反压 Provider 调度。
- 策略持久化为独立 JSON，运行状态只存 RAM；配置进程 watchlist 默认
  为空，只允许进程名，不接受路径、命令行或脚本。
- SQLite 升级到 schema v2，事件复用既有单 writer channel，使用确定性
  事件 ID 去重，并提供 366 天、2,000 条上限的查询；v1 预留表保留为归档。
- Named Pipe 增加 `queryDiagnostics`，Desktop 可读取当前活动告警；
  capabilities 明确 `actionsSupported=false`，v0.6 不提供任何优化动作。

潜在风险：

- 默认阈值是产品策略，不是所有硬件的物理极限；部署前应按工作负载校准，
  并在修改规则语义时同步提升 `ruleVersion`。
- raw snapshot 只保留 48 小时，超过该窗口只能审阅已持久化事件，不能从
  原始快照重新计算全部细节。
- SQLite 不可用时最近事件仍在有界 RAM 中，但重启后无法恢复未落盘事件。

回退：

- 可回退到 `v0.5.0`，但必须保留并建议先备份
  `%LOCALAPPDATA%\PerfMonitor\data\history-v1.db`。v0.5 不消费诊断
  schema v2，重新升级后恢复读取；不得以删除数据库作为 migration 回退。

## 0.5.0

变更点：

- 产品路径切换为独立的 `.NET 10` Agent 与 WPF Desktop；Desktop 仅通过
  contract v1 IPC 访问 Agent，退出或断线不影响采样与历史写入。
- 新增按当前用户 SID 派生的 Windows Named Pipe，使用受保护 DACL，仅
  允许当前用户和 LocalSystem，并显式拒绝 Network SID；协议实现 4 字节
  小端 framing、4 MiB 分配前上限、JSON 深度限制、hello/版本协商、
  request deadline、唯一 request ID、能力发现和 latest-wins 订阅。
- 新增 SQLite schema v1、显式 migration、完整性检查、WAL、单写者有界
  队列和独立读连接；raw/分钟/小时数据默认保留 48 小时/30 天/366 天，
  rollup 保留 `min/max/avg/last/count`。
- 历史查询强制时间范围、指标白名单、最多 16 个指标、5,000 点和 366 天，
  SQL 侧降采样保留尖峰；SQLite 初始化或写入失败只降级历史，不反压
  Provider 或实时快照。
- Desktop 自动重连，断线期间保留最后快照，并在 `instanceId` 改变时记录
  Agent 重启；增加真实 Named Pipe 重启测试和 Agent/Desktop/SQLite
  生命周期冒烟。
- 默认构建与启动不再打包或运行 Python 高频 Agent。Python 0.3 仅保留为
  oracle、fixtures 与显式 `--dev-http` 开发模式；产品 manifest 对全部
  .NET 文件和锁文件生成 SHA-256。

潜在风险：

- 当前 Desktop 为首版原生 WPF 状态页，尚未覆盖 Python Web UI 的全部
  进程排行与图表交互；扩展 UI 不得绕过 IPC 直接访问 SQLite 或 Win32。
- SQLite schema v1 只有前向 migration；1.0 前仍需完成异常退出恢复、
  升降级与 migration 回滚矩阵。
- 跨用户隔离已做 DACL 结构测试，仍需在 1.0 支持矩阵中执行真实多会话
  安装验证。

回退：

- 可回退到 `v0.4.0`；旧版本忽略但不得删除
  `%LOCALAPPDATA%\PerfMonitor\data\history-v1.db`。Python oracle 的最后
  产品回退标签仍为 `v0.3.0`。

## 0.4.0

变更点：

- 新增 `net10.0-windows` Agent、Core 和 Windows Collectors，首批实现系统
  CPU、内存、卷容量、uptime、全量进程和 Agent 自监控。
- Provider 改为独立绝对期限、有界并发、同 Provider 不重入、局部超时和
  有界指数退避；原子快照保持 availability/freshness/coverage 正交。
- 进程 CPU 继续以 `(PID, creationTimeTicks)` 做差分，输出整机归一化与
  核心等效口径；Idle 伪进程不计入覆盖率分母。
- 新增 Python/.NET 同窗差分门禁，CPU/内存均值门限为 3/1 个百分点，并
  精确比较单位、source ID、卷总容量、空值和进程身份。
- 新增 259,200 周期与快照替换虚拟门禁、短时真实进程资源门禁和默认
  72 小时的可执行发布长稳脚本；Agent 增加 `--quiet` 避免长稳期间复制
  控制台输出。
- CI 发布并冒烟测试 Agent，执行差分与加速长稳，并在上传前用 JSON Schema
  校验快照、差分和资源证据；Python 0.3 正式进入只读维护。

潜在风险：

- 当前 Agent 仍以 JSONL 作为旁路验证出口，没有 Named Pipe、SQLite 或
  Desktop 生命周期；不能把它当作 0.5 的最终进程间架构。
- CI 的加速资源门禁和 259,200 周期虚拟测试不等于真实墙钟 72 小时；正式
  发布证据必须由 `run_agent_soak.ps1` 产生且
  `actualWallClockPassed=true`。
- 不响应取消的第三方硬件 SDK 仍需在 0.7 放入独立 Worker；0.4 仅承载
  受控的 Windows Provider。

回退：

- 可回退到 `v0.3.0`；本阶段没有 SQLite schema、IPC 或安装格式迁移。

## 0.3.0

变更点：

- 冻结与产品版本解耦的 `contractVersion=1.0`，建立 snapshot、history、
  capabilities、health、error 和兼容 stats 的 JSON Schema。
- 定义稳定 metric/provider/source ID、单位、质量三维模型和跨语言错误码；
  Python/C# 异常类名不再进入新契约。
- 增加 scheduled/started/completed UTC 与 per-provider 观测时间，进程身份契约
  包含 PID 和 creation time。
- 增加正常、partial、stale、permission denied 和 PID reuse/process churn
  golden fixtures，Python 输出需通过 Schema。
- 增加 `net10.0` Contracts DTO 和 .NET fixture 反序列化/未知字段兼容测试。
- 采用 ADR-002～007，冻结进程权限、Provider 调度、SQLite、Named Pipe 和
  Python 退役门禁。

潜在风险：

- `/api/v1/*` 从 0.2.1 的过渡结构切换到正式 contract v1；仓库内前端已
  迁移，外部实验客户端应按 Schema 更新。
- Python 仍是同步参考采集器，独立 Provider 调度要到 0.4.0 的 .NET Agent
  实现，不能误认为 ADR 已经等同运行时代码。

回退：

- 可回退到 `v0.2.1`；本阶段没有 SQLite 或安装数据迁移。

## 0.2.1

变更点：

- 将实时快照、历史查询和能力发现拆分为版本化接口；旧
  `/api/stats` 保留为有界的 deprecated 适配器。
- RAM 历史增加 86,400 点绝对上限，历史查询增加 5,000 点硬上限，并按
  时间桶保留 `min/max/avg/last`。
- 浏览器只在首次连接时加载历史，之后每秒只获取快照并追加新点。
- 用 `availabilityStatus + freshness` 同时表达部分可用和陈旧状态。
- 实例状态增加随机 `instanceId`；端口冲突不再凭 `service` 字符串识别
  已有实例。
- 安装脚本强制验证 Python 3.12；CI 增加 EXE 构建、健康接口、自动退出和
  产物上传。
- 构建清单增加 Git commit、dirty 状态、依赖锁哈希、Python 架构和
  PyInstaller 版本。

潜在风险：

- `/api/stats` 的历史在超过默认查询上限时会降采样；依赖逐点完整历史的
  非官方客户端应迁移到 `/api/v1/history` 并显式指定范围。
- 0.2.1 仍是 Python 参考实现；后续只用于契约和指标口径对照，不继续增加
  GPU、温度、告警或优化动作。

回退：

- 源码与运行产物可回退到 `v0.2.0`；0.2.1 没有持久化格式迁移。

## 0.2.0

变更点：

- 用单进程 Windows 原生 API 后端替换 PowerShell/WMI 高频轮询。
- 修复进程工作集字段，并增加私有内存口径。
- 进程 CPU 增加整机归一化与核心等效两种明确口径。
- 使用单调时钟固定周期调度，历史窗口改为按时间戳裁剪。
- 增加原子快照、序号、时间、数据年龄、抖动、缺口和错误状态。
- 消除浏览器重复打开，增加已有实例识别和端口冲突退出码。
- 增加日志、依赖锁定、测试、版本资源与可验证构建脚本。
- 将采集、调度、存储和 HTTP 服务从入口文件中解耦。

潜在风险：

- `psutil` 进程私有内存在不同 Windows/psutil 版本上的可用字段可能不同；
  不可用时必须保持 `null`。
- 高频进程枚举的成本与进程数量相关，默认周期冻结为 1 秒；需在基准机型
  上继续验证后台 CPU P50/P95。
- 0.2 仍使用浏览器作为 UI，尚无 SQLite 持久化和正式 IPC 权限模型。

回退：

- 运行级回退使用构建前保留的 `dist\perf-monitor-0.1-legacy.exe`。
- 源码级回退应在首次 Git 审阅提交后通过版本标签进行；当前不得把未审阅
  的工作树直接视为发布基线。
