# 项目分析报告

最新开发分支技术审查｜轻量化、智能优化与可扩展性

技术评审文件 / 2026-10-02

> **结论**
>
> 应在现有 .NET 产品架构上增量开发，而不是重新规划 Python → .NET 迁移。当前已具备采集、IPC、WPF、SQLite、确定性诊断及可选 Broker；主要缺口是运行开销收敛、数据质量一致性、场景与瓶颈识别、动作恢复及收益验证。[S03][][S26]

| **项目** | **本次基准** |
| --- | --- |
| 仓库 | Wanchenrui/windows-performance-monitor |
| 分析分支 | codex/v1.0.0-release-architecture |
| 固定提交 | 74d29ddfc8a29fac23af5d57068b2aef08c42804 |
| 提交时间 | 2026-07-30 16:43:28 UTC |
| 版本性质 | 1.0.0 开发候选；不是已获生产批准的正式版本 |
| 核查日期与交付范围 | 2026-10-02；只读源码审查、远程 CI 核对、分析与规划文件 |

分支选择依据是所有可见分支 head 的提交时间，而非仅按版本名称排序；本次后续源码读取均使用上述完整 SHA。[S01][][S02]

## 三个目标的当前判断

| **目标** | **已有基础** | **当前仍需完成** |
| --- | --- | --- |
| 轻量化 | 分频采集、有界队列、UI 独立、Worker 隔离 | 消除重复克隆/序列化；限制完整快照落盘；预算覆盖整个产品。 |
| 智能化 | 规则、滞环、防抖、证据窗、受控动作接口 | 由阈值告警升级为场景/瓶颈判断，并建立可撤销动作与收益闭环。 |
| 可扩展性 | 16 个项目、稳定契约、模块和权限边界 | 收敛散落的指标知识；降低 UI 与内部模型耦合；保持动作白名单独立。 |

## 优先级与事实边界

优先修正 F02～F04 的持久化、时间和缺测语义，再处理 F01/F05/F06 的开销与隔离风险。自动优化必须先满足 F08 的恢复条件。以上是基于源码的工程判断，不是已测得的 CPU、内存或游戏帧率结论。

本次未修改项目代码；本地为 Linux，未取得可用 Windows/.NET 目标构建环境。远程 CI 状态已核对，但本次未重新执行构建、硬件测试、真实 72 小时测试或特权动作。

## 1 基准校正与审查方法

### 1.1 八个分支的 head 时间

| **分支（前缀 codex/ 省略）** | **head 简写** | **提交时间（UTC）** |
| --- | --- | --- |
| main | 9fca0fa58322 | 2026-07-29 11:08:39 |
| v0.4.0-agent-parity | b876ea7b6d5b | 2026-07-29 12:47:57 |
| v0.5.0-agent-desktop-ipc-sqlite | d4da421aba94 | 2026-07-30 02:37:42 |
| v0.6.0-diagnostics-alerts | 721c3b0ac4a7 | 2026-07-30 03:20:02 |
| v0.7.0-system-telemetry | e2b4c5f02a01 | 2026-07-30 03:47:18 |
| v0.7.1-hardware-provider-worker | 6584c958b885 | 2026-07-30 05:27:58 |
| v0.7.2-broker-whitelist-actions | 35110448747c | 2026-07-30 07:02:26 |
| v1.0.0-release-architecture | 74d29ddfc8a2 | 2026-07-30 16:43:28 |

来源为分支列表及各 head 对应的 Git commit 元数据。报告锁定最新时间的开发分支；没有将 main 的 0.3 行为混入当前产品路径。[S01][][S02]

### 1.2 对上一轮分析的明确修正

“尚未实现 Agent、Desktop、SQLite、独立 Provider 调度、诊断和 Broker”的表述只适用于旧 main 基线，不适用于本次分支。当前 Python 文件仍保留，但默认发布路径已是 .NET；Python 线程预热问题不能继续作为当前产品的首要瓶颈。[S03][][S04]

当前 ProcessProvider 输出所有可读进程，并非原 Python 界面的 Top 5。当前还已有网络、磁盘 I/O、电源、GPU 与温度能力，不能继续按“只有 CPU、内存和磁盘容量”的监控范围规划。[S07][][S17][][S03]

### 1.3 证据等级

| **等级** | **本报告含义** |
| --- | --- |
| 源码确认 | 已经读到具体实现和上下游调用；能说明逻辑行为，但不代表目标机已触发。 |
| 条件性风险 | 给出明确触发条件和代码推导；必须通过定向故障注入或 Windows 测量确认影响。 |
| 远程验证记录 | GitHub run/job/step 元数据可核对；不等同本次本地执行或已取得原始数值。 |
| 规划建议 | 新的产品目标、预算、工作包及验收方式；不是当前已实现或已达成能力。 |

审查沿 Agent 编排→采集/组装→诊断/存储/IPC→Desktop/动作边界展开，另核查发布架构和预算。未声称逐行审计所有文件、所有 P/Invoke、安装器及证书验证实现。

## 2 当前架构与应保留的实现

### 2.1 实际运行链路

> **监控数据路径**
>
> Windows Providers / 硬件 Worker → ProviderScheduler → SnapshotAssembler → AgentServiceRunner 的快照发布 → SQLite、DiagnosticEngine、Named Pipe 订阅 → 独立 WPF Desktop。[S04][][S05][][S06]

> **受控动作路径**
>
> IPC executeAction → AgentQueryService / AgentActionGateway → Broker 客户端 → Broker 的策略、身份与审计协调 → 一个类型化系统动作。诊断事件当前不会自动接入该链路。[S04][][S14][][S15]

| **边界** | **已实现且应保留** | **当前验收注意点** |
| --- | --- | --- |
| 采集与调度 | 独立周期、绝对期限、非重入、失败退避 | 共享并发槽仍有耗尽条件；不能认为取消令牌必然终止原生调用。 |
| 快照 | 引用发布、分组质量、进程实例身份 | 名义上的原子缓存仍在读取时重建并克隆；载荷不是深度不可变。 |
| 存储 | 有界 TryWrite、单写者、分级保留、回放 | 完整快照落盘范围与标量白名单不同；持久化质量/时间要核对。 |
| 诊断 | 确定性规则、滞环、防抖、证据窗 | 缺测恢复判断与置信度语义需完善；不等于场景优化。 |
| Worker | 厂商依赖隔离、响应复用、资源与重启约束 | 硬件能力及成本必须按实际设备验证。 |
| 动作 | 默认禁用/dry-run、审计、幂等、不确定状态 | 不是租约式优化，也没有收益评估与自动恢复闭环。 |

上述实现分别见采集与调度、存储、诊断、Worker 和动作源码；正式生产约束另见发布文档。[S06][][S08][][S11][][S16][][S14][][S19]

### 2.2 不应再做的架构重复建设

不需要再创建另一套 Agent、诊断事件总线、数据库服务或“通用优化框架”。继续使用 Core、Diagnostics、Actions、Collectors.Windows、Collectors.Worker、Storage.Sqlite、Ipc.NamedPipes 与 Desktop，按具体职责修改。新增程序集应由独立部署、权限或真正的依赖隔离需求驱动，而非目录美观。[S26]

现有 Broker/Worker 进程边界有实际价值：特权与厂商代码故障不能污染 UI 或普通权限 Agent。进程数不是唯一轻量化指标，应同时衡量每个组件的必要性、存活时间和总成本。

## 3 轻量化发现：重复工作与数据放大

### F01 无输出仍序列化；无变化组被反复克隆｜P1

源码确认：AgentServiceRunner.WriteSnapshotAsync 先执行校验与 AgentJson.Serialize，之后才判断 quiet 和 outputPath。因此在 quiet=true 且没有文件输出时，完整序列化结果仍被创建后丢弃。该条件可直接复现；quiet 不是 AgentOptions 的默认值，报告不将它误写成无条件默认配置。[S04][][S24]

源码确认：SnapshotAssembler 发布一个 Provider 结果时，先克隆该结果，再在 BuildSnapshot/BuildGroup 中克隆全部组。Read 又在锁内重建全快照。健康查询同样调用 Read，因此轻量健康请求也会经过完整数据树处理。[S05][][S15]

放大关系：设进程行数为 N，所有 Provider 每秒发布次数为 P，外部读次数为 R。重复组装的主要数据遍历量可随 (P＋R)×N 增长，而不是只随进程 Provider 的 2 秒周期增长。这是路径复杂度分析，不是实测分配量或耗时。

| **最小修改方向** | **必须守住的边界** |
| --- | --- |
| 无控制台/文件输出时提前退出输出函数 | 协议校验不能全局删除；把不变条件校验放到注册或启动阶段。 |
| 每个 Provider 发布后拥有不可变结果；未变化组复用 | 不能只删除 DeepClone 后暴露可写 JsonNode；先明确所有权与不可变载荷。 |
| 年龄和质量与重载荷分开更新 | 读取不得混合不同发布批次；sequence、instanceId 和 per-group 时间保持一致。 |
| 健康接口只读取必要的健康投影 | 仍须反映最新可用性、陈旧度和采集故障，不以缓存隐藏故障。 |

### 进程采集本身：先测调用和分配，再换实现

当前每 2 秒 Process.GetProcesses 后读取各进程创建时间、CPU 时间、名称、工作集和私有内存，并生成全部进程的 JsonArray。身份和 CPU 单调时间差分逻辑应保留。[S07]

优先测量名称/元数据查询、JsonNode 分配、全量传输与克隆成本。按进程实例缓存低变化元数据；用受限展示投影减少 UI 传输，但保留发现新热点的低成本全局采集。不要为了 Top N 展示而失去全局可见性，也不要未经比较就改成另一套原生调用。

### 验收

比较同一 Windows 工作负载下的 CPU 时间、分配字节/秒、GC、锁等待、快照读取 P95/P99 与 IPC 字节数；覆盖 100/500/1000 个进程、无 UI/单 UI/慢客户端。新增测试需证明无输出路径不再序列化，并验证读取结果不可被消费者修改。

## 4 轻量化与隐私发现：持久化边界

### F02 标量白名单没有限制完整快照落盘｜P1

源码确认：SnapshotMetricExtractor 仅过滤 metrics_raw 使用的标量；SqliteHistoryStore.InsertSnapshotAsync 另将整个 AgentSnapshot 序列化到 snapshots_raw.snapshot_json。完整进程列表和硬件 devices 明细因此在默认数据链路上进入持久化，而不是被标量白名单过滤。[S08][][S09][][S17]

这与 README“设备明细不写 SQLite”的声明不一致。本次确认的是代码路径与文档冲突，未在目标数据库中读取实际记录；不能据此推断存在遥测上传或外部数据泄露。[S03][][S08]

### 数据量与统计语义的两个风险

数据量估算应以实测单帧字节 B 为输入：每日 JSON 数据量约为 B×86400/T，T 为实际落盘间隔。仅作示例，100 KiB/帧、每秒一帧约为 8.24 GiB/日，尚未计入索引、标量、rollup、WAL 与数据库页开销。这不是本项目当前单帧大小或占盘的实测值。

当前 raw 保留 48 小时、队列容量 256、批上限 64；这些上限不能替代字节预算。256 个大快照可保留较大内存，48 小时全量 JSON 也可能远超普通用户期望。[S25]

标量写入使用快照 CompletedAtUtc，而不是各 Provider 的 observedAtUtc；提取器没有显式过滤 freshness。5 秒更新的指标可能在多个 1 秒快照中重复写入。应区分“最后值保持”与“新的实际观测”，并明确 count/avg 的统计口径，而不是把重复值自然当作新样本。[S08][][S09]

### 建议：最小回放投影，而非取消回放

| **持久化类别** | **建议边界** |
| --- | --- |
| 常规标量历史 | 只保留支持的指标、来源、质量、原始观测时间与有定义的聚合。 |
| 确定性诊断回放 | 保留规则依赖、必要上下文和受限关注对象；用回放夹具证明裁剪不改变事件。 |
| 完整诊断 trace | 显式启用、短时、容量硬上限；按隐私规则脱敏并可删除，不常年默认保存。 |
| 动作审计 | 沿用独立 Broker 数据库；必要恢复信息不得进入可丢弃遥测队列。 |

现有 writer 只是立即排空队列至批上限；低负载下不保证跨时间窗口合批。可在普通遥测 writer 内增加有上限的短时合批与预备语句复用，但必须保留单写者、错误可见及有限等待。[S08]

验收需检查真实数据库内容、单位时间写入量、队列字节峰值、回放一致性、WAL 恢复与迁移回退。应用进程崩溃测试不等于断电持久性测试；SQLite 的 WAL＋NORMAL 存在独立的断电边界。[S29]

## 5 正确性发现：时间、质量与缺测

### F03 陈旧度仍依赖墙上时钟｜P1

源码确认：采集调度使用单调时间，但 SnapshotAssembler 用 UTC 差值判断 fresh/stale；DiagnosticEvaluator 用观测 UTC 排序、防抖，并忽略不晚于上一时间的观测。时钟回拨后，陈旧度可能被低估，规则可能暂停推进，直至时间重新超过旧值。[S05][][S06][][S11]

这是明确触发条件下的逻辑风险，不是已在用户电脑上观察到的故障。简单将全部字段替换为本机 Stopwatch 值也不合适，因为持久化和跨实例回放仍需要可比较的时间语义。

建议内部年龄、持续时长、TTL 使用单调时间；UTC 用于展示和查询。对睡眠恢复、时钟调整、实例切换增加明确 epoch/gap 语义，必要时重新建立差分基线。持久化保存足够的逻辑顺序/经过时间信息，使离线回放不依赖回放电脑的当前时钟。

### F04 关注进程缺测回落到 0；质量判定不完整｜P1

源码确认：DiagnosticSnapshotReader.AddWatchedProcesses 对未读到有效 CPU 的名称执行 maxima\[watched\] ?? 0；IsReadable 只检查 availability 为 available/partial，不检查 freshness。因权限受限、首次采样或进程缺失而没有值时，0 可以进入恢复条件。[S10]

| **实际状态** | **建议诊断行为** |
| --- | --- |
| 有新鲜、有效的低 CPU 观测 | 满足恢复门限与完整恢复持续时间后，才发出 resolved。 |
| 首次采样、拒绝访问、部分覆盖中目标不可读 | unknown/warming-up；暂停相关动作，不当作数值 0，不宣称故障消失。 |
| 已确认目标进程实例退出 | 结束该实例的观察，记录退出原因；不伪造“CPU 降到 0”的实测恢复。 |
| 旧观测仍被引用 | 明确 stale；不把同一观测反复计为新的证据或持续恢复样本。 |

关注规则当前按进程名聚合最大 CPU，subject 为 process:名称，而非唯一进程实例。用于提示可以保留该口径；进入动作时必须重新解析、校验 PID＋创建时间与归属，不可直接按名称推导动作目标。[S10][][S14]

### 应新增的针对性测试

覆盖时钟前跳/回拨、睡眠恢复、权限从可读转为受限、初始 CPU=null、进程退出和 PID 复用。重点验收：缺测不能产生伪造低值或错误 resolved；规则回放仍确定；旧数据不能为新的系统改动提供授权依据。

## 6 隔离与交互发现

### F05 非合作调用可耗尽共享并发槽｜P1，条件性风险

源码确认：ProviderScheduler 超时后仍保留尚未结束调用占用的 semaphore 槽；这是避免失控并发的正确做法。但所有 Provider 共享同一 semaphore，如果不结束的调用数量达到 maxConcurrency，健康 Provider 也会等待槽位，其采集超时尚未开始计时。[S06]

默认 maxConcurrency=5 已为两个硬件指标与基础 Provider 留出余量。因此，单个硬件调用卡住不等于整机采样必然停止；本报告指出的是多个非合作调用累积或较小并发配置下的隔离边界，不声称当前默认场景必然触发。[S24]

不要用“超时就释放槽位并启动下一个任务”修复，否则可能制造无界原生调用。优先为基础轻量采样保留明确预算，对排队等待设跳过/超时语义；确有不可终止风险的模块放入现有 Worker 式进程边界。测试应同时注入 N 个不合作 Provider，并证明基础组仍按规定更新。

### F06 IPC 最新值队列没有延伸到 UI 队列｜P2

源码确认：DesktopAgentSession 接收快照后发出 StateChanged；MainWindow 每次都调用 Dispatcher.InvokeAsync，未合并排队渲染。UI 线程阻塞时，排队闭包可能保留多个完整快照，即使 IPC 侧只有一个 latest-wins 槽也不能消除该队列。[S12][][S13]

该风险符合 WPF Dispatcher 的排队执行模型；本次没有实际观测到内存泄漏。建议 UI 侧保留一个最新状态和一个待渲染标志；渲染期间到达的更新覆盖旧状态，不再逐帧排队。[S27]

### 界面能力与产品目标之间的距离

| **当前实现** | **后续最小产品化补齐** |
| --- | --- |
| CPU/内存/网络/磁盘/GPU/温度卡片 | 按组显示 unavailable、partial、warming-up、stale 及原因；不可只展示数字。 |
| 历史按钮只显示时间桶/源样本数量 | 增加有界历史图，使用 min/max 包络保留尖峰，按实际观测时间显示缺口。 |
| 诊断按钮显示活动数和最近 ruleId | 展示证据窗、目标、判断限制与建议；再提供授权和恢复入口。 |
| 每次接收均投递渲染 | 最小化/隐藏时暂停可视刷新，恢复后获取最新状态；不停止 Agent。 |

上述当前行为直接来自 MainWindow；建议在 Desktop 内提取展示状态和 ViewModel，不必更换 WPF 或引入新的 UI 服务层。[S13]

## 7 智能化发现：告警不等于优化闭环

### F07 规则成立不等于瓶颈或收益可信｜产品缺口

当前规则以高 CPU、物理内存比例、系统盘容量、指定名称进程 CPU 等阈值为中心。它们能说明“某个条件持续成立”，不能单独判定游戏、编译、会议等场景，也不能证明应该调整哪一个进程。[S10]

DiagnosticEvaluator 的 Confidence 固定为 1，含义来自完整 debounce 后的确定性状态转换，不能被后续模块解释为“瓶颈原因有 100% 概率正确”或“动作一定有效”。应保留已冻结字段语义，另用明确字段表达诊断可信程度、证据完整性或模型校准结果。[S11]

| **场景目标** | **还需要的证据** | **不能据此承诺的结论** |
| --- | --- | --- |
| 游戏/交互稳定 | 前台实例、帧时间或响应指标、资源争用 | GPU 最大 load 或高 CPU 不足以证明某后台任务导致掉帧。 |
| 编译/渲染吞吐 | 任务身份、完成时间、负载阶段 | CPU 高可能正是充分利用资源，不能因告警而降速。 |
| 内存压力 | 提交量/限额、缺页与 I/O 相关性 | 物理内存占用比例高，不等于应清空工作集。 |
| 电池/热约束 | 供电状态、有效的设备级温度及适用约束 | 所有温度最大值不是统一的 CPU 温度或统一硬件极限。 |

现有 GPU/温度聚合与电源数据可作为部分输入，但不应改变现有 metricId 的含义来冒充缺失指标。[S17][][S03]

### F08 现有审计不能代替自动恢复｜自动动作前置门禁

当前 Broker 协调器已实现验证、pending 预留、幂等重放限制、CaptureBefore、执行以及最终结果记录。异常后保留 indeterminate，避免盲目重复动作，这是应保留的安全基础。[S14]

但 CaptureBefore 得到的原状态通过最终 Complete 才写入结果；已审查接口没有“在动作前单独持久化原值和恢复租约”的步骤。也没有场景结束恢复、租约到期恢复、用户改动冲突判断或收益评估。因此它是受控动作设施，不是已经完成的自动优化器。[S14]

下一步必须先实现可撤销动作的归属、原状态、应用状态、租约及异常恢复，再开放 opt-in 自动动作。terminate_process 不能被包装成可撤销优化，也不应成为默认自动策略。

## 8 可扩展性与资源门禁

### F09 模块边界已经存在，指标知识仍有散落｜P2

当前 src 已有 16 个项目。Provider 接口及能力描述为扩展打下基础，但 Agent 的必需组校验、诊断 Reader 的规则入口、历史指标白名单以及 Desktop 的卡片和字段读取仍各自包含具体指标知识。新增能力可能需要同时修改多个层级。[S26][][S04][][S10][][S09][][S13]

建议在现有 Contracts/Core 中统一 metricId、单位、组归属、持久化与展示元数据的注册入口；仍由各业务模块定义自己的规则和视图。只抽取真正重复的定义，不把所有对象改成字符串反射，不新增服务总线。

AgentSnapshot 及 JsonNode 数据结构位于 Core，并被 Desktop 使用；核心调度模型、传输 DTO 与 UI 展示存在依赖交叉。可按实际变更逐步使用 Contracts 中的只读传输模型和 Desktop 展示模型，避免为解耦而一次性移动所有类型。[S05][][S12][][S13]

Worker 当前是固定 collect 协议的受控实现，不能称为已开放插件系统。第三方扩展应先保持只读、限权、隔离和资源上限；动作注册仍是独立白名单，不能因新增采集插件而自动获得系统修改能力。[S03]

### F10 现有预算不足以证明整套产品轻量｜P1

| **冻结的 Agent 门限** | **当前值（非实测）** |
| --- | --- |
| CPU 单核等效均值 | 20% |
| 工作集峰值 / 私有内存峰值 | 各 256 MiB |
| 保留私有内存增长 / GC 堆增长 | 32 MiB / 16 MiB |
| 句柄峰值 / 保留句柄增长 / 线程峰值 | 768 / 64 / 64 |

资源文件目前只包含 agent。20% 单核等效在 8 个逻辑处理器上算术等价于整机归一化 2.5%，不能直接写成“整机 CPU 20%”或误认为 0.2%。该预算未同时约束 Desktop、Worker、Broker 及总磁盘写入。[S18]

后续应区分标准监控、硬件增强、可视 UI、诊断和动作模式，测量整个产品进程集合。先冻结实测基线再逐步收紧门限；不以关闭保护、遗漏指标、把开销转移到 Worker 或压低刷新真实性换取表面指标。

## 9 验证结论与实施优先级

### 9.1 本次实际取得的验证证据

| **检查项** | **已取得的结果** | **不能外推** |
| --- | --- | --- |
| 最新基准 | 8 个分支 head 时间已核对，固定 SHA 已明确 | 不是 main 0.3，也不表示该分支已正式发布。 |
| 远程 CI | run 30562783265；三个 job 均 completed/success | 没有本次本地构建或目标机运行数值。 |
| 测试范围 | Python、.NET/契约、差分、虚拟/加速测试、构建冒烟、WAL 崩溃恢复、Broker dry-run | 虚拟/加速测试不能代替真实 72 小时；dry-run 不能代替实际特权动作验证。 |
| 原始产物 | 当前 artifact API 列表为空 | 不否认过去上传；本次无法复核原始数值、二进制及报告内容。 |
| 生产证据 | 已读发布门禁和支持矩阵定义 | 未取得完整四机安装证据、受信任生产签名及最终候选真实长稳证据。 |

对应远程记录与文档见 [S21][][S22][][S23][][S19][][S20][][S30]。本次未触发 CI 重跑，未修改保护规则、分支、系统参数或设备。

### 9.2 优先级与下一轮建议

| **顺序** | **要解决的问题** | **后续规划书对应** |
| --- | --- | --- |
| 先校正事实与质量 | F02 持久化边界；F03 时间；F04 缺测语义 | WP-02、WP-03 |
| 再收敛运行成本 | F01 克隆/输出；F05 隔离；F06 UI 队列；F10 全产品预算 | WP-01、WP-04、WP-05 |
| 再开放智能建议 | F07 场景、瓶颈、证据和效果指标 | WP-06 |
| 最后受控自动化 | F08 原状态持久化、租约恢复与收益验证 | WP-07、WP-08 |
| 按需求扩展 | F09 指标定义收敛、展示与受控扩展 | WP-05、WP-09 |

发布文档绑定的 v0.4 历史 72 小时基线有回归参照价值，但不能单独代表包含 Desktop、Worker、诊断、存储和 Broker 的最终 v1 候选。规划应增加最终候选二进制哈希和实际启用模式绑定的长稳证据，不删除已有历史基线门禁。[S19]

> **决策建议**
>
> 不重做已存在的架构，不立即打开自动调参。先完成一轮“质量修正＋低开销数据链路＋全产品实测”，再以建议模式积累场景证据；只有恢复机制和收益验证通过，才允许逐项启用自动动作。

## 附录 证据索引

仓库代码链接均固定到本报告基准提交；GitHub 分支、CI 与 artifact 列表为核查时的远程状态。引用编号可直接点击。

[\[S01\] 分支列表与 head；逐项核对提交时间][S01]

动态元数据，核查日 2026-10-02；各 head 的 Git commit 时间用于判定最新分支。

[\[S02\] 固定提交及版本说明][S02]

提交时间 2026-07-30 16:43:28 UTC；Require continuous coverage for 72-hour soak。

[\[S03\] README.md][S03]

候选产品路径、指标范围、默认安全边界；文档声明不能代替运行验证。

[\[S04\] Agent/AgentServiceRunner.cs][S04]

RunAsync、WriteSnapshotAsync、ValidateCoreGroups：生产编排、输出与固定指标组。

[\[S05\] Core/SnapshotAssembler.cs][S05]

PublishAsync、BuildGroup、Read/RefreshAgeAndFreshness：深拷贝、锁、UTC 陈旧度。

[\[S06\] Core/ProviderScheduler.cs][S06]

RunProviderAsync、CollectIsolatedAsync：绝对期限、共享并发槽、超时后在途调用。

[\[S07\] Collectors.Windows/ProcessProvider.cs][S07]

2 秒全进程采集；PID＋创建时间；输出全部可读取进程。

[\[S08\] Storage.Sqlite/SqliteHistoryStore.cs][S08]

TryPublish、RunWriterAsync、WriteBatchAsync、InsertSnapshotAsync、回放读取。

[\[S09\] Storage.Sqlite/SnapshotMetricExtractor.cs][S09]

标量白名单提取不等于完整 snapshot_json 的持久化过滤。

[\[S10\] Diagnostics/DiagnosticSnapshotReader.cs][S10]

规则入口、IsReadable、AddWatchedProcesses；缺测值回落到 0。

[\[S11\] Diagnostics/DiagnosticEvaluator.cs][S11]

滞环、debounce、cooldown、确定性事件；Confidence 固定为 1。

[\[S12\] Desktop/DesktopAgentSession.cs][S12]

订阅、重连和 StateChanged；连接与 UI 生命周期分离。

[\[S13\] Desktop/MainWindow.cs][S13]

Dispatcher 投递、指标读取、历史数量摘要及诊断摘要。

[\[S14\] Actions/ActionExecution.cs][S14]

BrokerActionCoordinator、IActionAuditStore：pending、幂等、执行前后状态与不确定结果。

[\[S15\] Agent/AgentQueryService.cs][S15]

健康查询读取完整快照；能力由描述符及静态目录共同生成；动作网关入口。

## 附录 证据索引（续）

仓库代码链接均固定到本报告基准提交；GitHub 分支、CI 与 artifact 列表为核查时的远程状态。引用编号可直接点击。

[\[S16\] Collectors.Worker/HardwareWorkerCoordinator.cs][S16]

单客户端串行化、250 ms 响应复用和引用计数释放。

[\[S17\] Collectors.Worker/HardwareWorkerProviders.cs][S17]

GPU/温度 5 秒周期；devices 明细进入快照；load/temperature 最大值口径。

[\[S18\] release/resource-budgets-v1.json][S18]

仅 Agent 的冻结资源门限；不是实测资源占用。

[\[S19\] docs/architecture/v1.0-release-architecture.md][S19]

安装、签名、来源证明和历史 v0.4 72 小时证据绑定规则。

[\[S20\] docs/support-matrix-v1.md][S20]

项目声明的四目标 x64 支持范围；目标框架版本不等于支持承诺。

[\[S21\] Windows CI：精确 run 30562783265][S21]

当前可核对结果为 completed/success；三个 job 均成功。

[\[S22\] 同一 CI run 的 job/step 元数据][S22]

Python、.NET/差分/虚拟及加速测试、构建/冒烟/崩溃恢复/Broker dry-run。

[\[S23\] 同一 CI run 的 artifact 列表][S23]

核查时 total_count=0；不能据此否认历史上传，也不能读取已不在列表中的测量数据。

[\[S24\] Agent/AgentOptions.cs][S24]

默认输出周期 1 秒、warmup 3 秒、maxConcurrency=5；quiet 由参数指定。

[\[S25\] Storage.Sqlite/SqliteHistoryOptions.cs][S25]

队列 256、批上限 64、busy_timeout 1 秒；raw 48 小时、分钟 30 天、小时 366 天。

[\[S26\] src 项目目录][S26]

16 个现有项目；模块与进程边界均已存在。

[\[S27\] Microsoft：WPF Threading model][S27]

Dispatcher 排队与 UI 线程执行模型；核查日 2026-10-02。

[\[S29\] SQLite：PRAGMA synchronous][S29]

WAL＋NORMAL 的一致性与断电持久性边界；不能将进程崩溃测试等同断电验证。

[\[S30\] release/evidence 目录][S30]

当前树可见 v0.4-agent-baseline-candidate.json；本次未取得真实 72 小时结果数据。

  [S03]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/README.md
  [S26]: https://github.com/Wanchenrui/windows-performance-monitor/tree/74d29ddfc8a29fac23af5d57068b2aef08c42804/src
  [S01]: https://api.github.com/repos/Wanchenrui/windows-performance-monitor/branches?per_page=100
  [S02]: https://github.com/Wanchenrui/windows-performance-monitor/commit/74d29ddfc8a29fac23af5d57068b2aef08c42804
  [S04]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Agent/AgentServiceRunner.cs
  [S07]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Collectors.Windows/ProcessProvider.cs
  [S17]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Collectors.Worker/HardwareWorkerProviders.cs
  [S05]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Core/SnapshotAssembler.cs
  [S06]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Core/ProviderScheduler.cs
  [S14]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Actions/ActionExecution.cs
  [S15]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Agent/AgentQueryService.cs
  [S08]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Storage.Sqlite/SqliteHistoryStore.cs
  [S11]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Diagnostics/DiagnosticEvaluator.cs
  [S16]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Collectors.Worker/HardwareWorkerCoordinator.cs
  [S19]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/docs/architecture/v1.0-release-architecture.md
  [S24]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Agent/AgentOptions.cs
  [S09]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Storage.Sqlite/SnapshotMetricExtractor.cs
  [S25]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Storage.Sqlite/SqliteHistoryOptions.cs
  [S29]: https://www.sqlite.org/pragma.html#pragma_synchronous
  [S10]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Diagnostics/DiagnosticSnapshotReader.cs
  [S12]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Desktop/DesktopAgentSession.cs
  [S13]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Desktop/MainWindow.cs
  [S27]: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model
  [S18]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/release/resource-budgets-v1.json
  [S21]: https://github.com/Wanchenrui/windows-performance-monitor/actions/runs/30562783265
  [S22]: https://api.github.com/repos/Wanchenrui/windows-performance-monitor/actions/runs/30562783265/jobs?per_page=100
  [S23]: https://api.github.com/repos/Wanchenrui/windows-performance-monitor/actions/runs/30562783265/artifacts?per_page=100
  [S20]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/docs/support-matrix-v1.md
  [S30]: https://github.com/Wanchenrui/windows-performance-monitor/tree/74d29ddfc8a29fac23af5d57068b2aef08c42804/release/evidence
