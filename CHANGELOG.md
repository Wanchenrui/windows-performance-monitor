# 变更记录

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
