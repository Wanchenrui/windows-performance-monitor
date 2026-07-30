# 变更记录

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
