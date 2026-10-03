# 后续开发规划书

基于现有 v1.0 候选的增量路线｜工作包、依赖、验收与回退

技术评审文件 / 2026-10-02

> **实施主线**
>
> 先修正质量与持久化语义，建立整套产品的性能基线；再消除明确开销、增强场景诊断；最后引入可撤销、可验证的自动优化。保持现有 .NET 模块化单体和 Broker/Worker 边界，不另起重构工程。

| **规划约束** | **约定** |
| --- | --- |
| 唯一实现基准 | codex/v1.0.0-release-architecture 74d29ddfc8a29fac23af5d57068b2aef08c42804 |
| 基准提交时间 / 核查日期 | 2026-07-30 16:43:28 UTC / 2026-10-02 |
| 关联文档 | 《项目分析报告》同一 SHA；发现编号 F01～F10 用于需求追踪。 |
| 本次性质 | 分析与建议，不包含源代码修改；里程碑、参数目标尚未执行。 |
| 版本安排 | 使用 M0～M4 里程碑，不把仓库已有 0.4～0.7 工作重新列为待实现，也不擅自声明新版本已发布。 |

## 目标不是“占用越低越好”，而是目标任务得到收益

| **目标** | **可验收的定义** |
| --- | --- |
| 轻量化 | 给定硬件、进程规模与启用模式，后台总 CPU、内存、写入和唤醒受控；真实任务没有不可接受的监控干扰。 |
| 智能化 | 能区分场景与瓶颈；证据不足时不动作；每次修改有原因、授权、期限、结果和恢复状态。 |
| 可扩展性 | 新增受控采集能力无需改采样主循环；新规则不直接访问 Win32；UI 不承担采集与策略；扩展不能绕过动作权限边界。 |

## 交付节奏

M0 固定基线与证据 → M1 质量和轻量化收敛 → M2 场景诊断/建议 → M3 低风险自动优化 → M4 受控扩展与本机自适应。并行开展 UI 展示与证据工程，但 M3 必须等待 M1/M2 和恢复门禁通过。

目前没有指定团队容量、最低硬件、支持场景优先级和生产签名条件，故不填写看似精确的日历工期。各阶段以下述入口/出口门禁推进；研发负责人在 M0 固定人力与目标设备后再排期，不以缺少这些信息阻塞独立的质量修正。

已有实现及发布候选性质见 [S03][][S19][][S26]。该规划不授权推送、合并、发布、安装服务或更改任何在线运行参数。

## 1 工作包与依赖：优先完成现有路径

| **工作包 / 优先级** | **落点与主要交付** | **验收出口** |
| --- | --- | --- |
| WP-00 / M0 | 固定 SHA、模式、硬件与 CI 证据目录；补齐原始性能基线。负责人：研发＋Windows 测试。 | 测试脚本、原始记录、二进制哈希和运行参数可关联；不将历史 CI 摘要当数值。 |
| WP-01 / P1 | Agent 输出提前短路；Core 快照所有权与增量组复用；健康轻量投影。负责人：Core/.NET。 | 无输出不序列化；不可变性与原子性通过；分配/读延迟下降且无指标语义回归。 |
| WP-02 / P1 | Storage.Sqlite 增加最小回放投影、观测时间/质量和字节边界；校正文档。负责人：存储。 | 真实数据库只包含允许内容；同一输入回放事件一致；迁移失败可恢复。 |
| WP-03 / P1 | Core/Diagnostics 修复时钟、缺测、预热、退出和恢复语义。负责人：诊断＋Core。 | 未知数据不填 0；时钟跳变与 PID 复用不误触发动作或 resolved。 |
| WP-04 / P1 | ProviderScheduler 保证基础组预算；扩展进程集合 Self Metrics 与超预算降级。负责人：Core/采集。 | 多个阻塞 Provider 下基础指标仍前进；总 CPU/内存/写入受预算约束。 |
| WP-05 / P2 | Desktop 合并渲染、隐藏降频、质量展示、证据/历史/动作状态界面。负责人：Desktop。 | UI 排队状态有界；恢复显示新鲜数据；不从界面直接调用 Broker/Win32。 |

WP-01 与 WP-03 可并行，但不可变载荷改动需要与 WP-02 的投影接口对齐。WP-04 先测量并修复明确风险，不以大量新增线程、队列或 Worker 替代成本分析。

### 智能化和扩展工作包

| **工作包** | **依赖** | **交付重点** |
| --- | --- | --- |
| WP-06 场景诊断 | WP-02、03；WP-05 配合 | 前台/任务上下文、瓶颈证据、规则回放集；先仅展示建议。 |
| WP-07 恢复机制 | WP-03；现有 Actions/Broker | 动作原值预写、租约、冲突判断、崩溃核对；仍保持默认 dry-run。 |
| WP-08 效果闭环 | WP-04、06、07 | 小范围 opt-in，固定工作负载 A/B，退化撤销与策略停用。 |
| WP-09 受控扩展 | M1 稳定；具体扩展需求 | 统一指标定义、第一方 Provider/规则接入；再评估第三方 Worker 协议。 |

模块定位与问题依据：[S04][][S05][][S06][][S08][][S10][][S13][][S14]。工作包是现有工程内的修改范围，不是新增系统的项目清单。

## 2 轻量化目标：从资源门限到产品预算

### 2.1 先统一测量口径

参考机建议固定为 8 个逻辑处理器、16 GiB RAM、SSD、约 500 个进程；记录 CPU 型号、Windows build、供电/电源模式和安全软件状态。最低支持机型另建基准，不将参考机目标外推为所有电脑的保证。

同时报告单核等效 CPU=100×ΔCPUTime/Δt，以及整机归一化 CPU=前者/N。统计所有产品进程的 CPU 时间总和；内存同时列私有提交与工作集，汇总工作集时注明共享页可能被重复计入。

| **指标** | **M1 首轮建议目标** | **适用条件** |
| --- | --- | --- |
| 后台 CPU | 参考机整机均值 ≤0.5%；1 秒窗 P95 ≤1.0% | 标准监控：UI 关闭，常规采集/存储/诊断启用。8 线程下均值对应单核等效 ≤4%。 |
| Agent 私有内存 | 预热后 P95 ≤96 MiB；正常稳态峰值 ≤128 MiB | 不以工作集修剪、强制 GC 或漏采数据达标。 |
| 标准后台集合私有内存 | 预热后 P95 ≤160 MiB | 包括启用但空闲的 Broker；硬件 Worker 单列增量，不能隐藏在集合之外。 |
| 稳态增长 | 72 小时首尾 20% 中位数差：私有内存 ≤16 MiB；无持续上升趋势 | 排除明确有界缓存建立期；同时检查峰值、斜率、句柄、线程和队列字节数。 |
| 持久化逻辑增量 | 标准模式最终数据库逻辑数据增量建议 ≤100 MiB/日 | 基于精简回放投影；另报告物理写入、WAL 峰值及保留后的总盘占。 |
| 任务干扰 | 监控引起的任务指标回退建议以 1% 作为首轮告警线 | 重复实验与噪声区间判断；不是一次测量超过 1% 就定性失败。 |

以上全是提议的工程目标，不是现有数值；先用 WP-00 测量，再由最低支持机型和产品取舍冻结。硬件增强、UI 可见、诊断 trace、动作执行分别给出增量预算，未测清前不能宣称这些模式满足标准模式门限。

### 2.2 沿用分频，不牺牲可观测性

| **对象** | **当前 / 建议** |
| --- | --- |
| 基础 CPU/内存、网络/磁盘 I/O | 以现有 1 秒采集为起点；场景诊断不得把全进程枚举统一提高到毫秒级。 |
| 全局进程 | 保留 2 秒动态采集；名称等低变化信息按实例缓存，设置容量与退出清理。 |
| 卷容量 / 硬件 | 沿用低频卷容量；GPU/温度当前 5 秒。只对确需的对象做限时高频诊断。 |
| 自我降级 | 先停非必要 trace/可视刷新，减少完整回放和可选 Worker；同时报告缺失能力与降级原因。 |

当前预算仅 Agent 单核等效均值 20%、峰值 256 MiB；应保留旧门禁作为回归参照，再加入模式化产品预算，不能直接把旧预算称作实测轻量化。[S18][][S07][][S17][][S03]

## 3 数据路径实施规格：复用而非重建

### 3.1 快照所有权与发布顺序

Provider 收集完成后构造其自有只读结果；Assembler 只替换该组，并生成轻量 envelope。未变组复用已冻结载荷；消费者没有修改权。将读取时的新鲜度投影与大数据树分离，健康接口不读取进程明细。

第一轮可保留现有 AgentSnapshot 外观与 contract v1，在内部替换载荷/所有权实现；对不得不变化的外部字段使用兼容新增。不要在原 metricId 下改变单位、归一化方法或统计口径。

顺序保持“更新最新状态 → 非阻塞 TryPublish 到各消费者”。UI 丢弃旧帧；存储按策略降级并统计 dropped；诊断输入缺口必须显式处理。动作恢复审计不复用这条可丢弃链路。[S04][][S05][][S08][][S14]

### 3.2 持久化投影的验收规格

| **对象** | **投影内容** | **容量/兼容控制** |
| --- | --- | --- |
| 指标历史 | metricId、unit、sourceId、质量、Provider 观测时间、有效数值 | 新观测去重；最后值保持单独定义，不重复计作独立观测。 |
| 诊断回放 | 所启用规则的必要输入、逻辑时间/gap、受限关注对象和上下文 | 同一规则版本在原始夹具与投影上产生相同事件；缺字段不能默认为正常。 |
| 设备/进程明细 | 默认不全量持久化；确需时按隐私策略选择字段 | 明确 opt-in、字节上限、TTL；保留删除入口及退出/实例变化处理。 |
| 数据库升级 | 显式 schema version、备份与可读性检测 | 失败不静默删除原库；旧二进制面对新 schema 不继续写入。 |

当前整帧 JSON 是回放来源，不能仅删去 snapshots_raw 而不提供替代回放数据。WP-02 的完成定义同时包含“数据减量”与“回放语义保持”。[S08][][S09]

### 3.3 成本控制与故障局部降级

普通遥测写入可使用有界时间窗合批和预备语句；建议初始合批窗口 2～5 秒，由实际写入/查询时效冻结，批大小和字节数双限。现有 batch=64 只是数量上限，并不保证低速输入下有时间合批。[S08][][S25]

增加 queueBytes、oldestPendingAge、writerLag、droppedByReason、DB/WAL 字节和 I/O 写入计数。数据库故障仍应保留实时快照；但若动作必要审计或恢复记录不可写，对应系统动作必须拒绝。

WAL 进程崩溃恢复和断电持久性分别测试；不要将 NORMAL 调整为 OFF 来追求低写入。特权审计按独立风险预算确定提交耐久性。[S29]

## 4 智能诊断：先解释，再建议

### 4.1 在现有 Diagnostics 内补齐三类输出

| **输出** | **内容** | **不能混淆** |
| --- | --- | --- |
| Scenario 场景 | 前台实例、用户选择的配置、供电状态、持续负载阶段 | 应用名称或单个阈值不能证明正在游戏、会议或编译。 |
| Diagnosis 诊断 | 可能瓶颈、依赖指标、证据窗、反证和质量限制 | 高 CPU、内存比例、温度最大值不是同一种瓶颈。 |
| Recommendation 建议 | 预期改善目标、候选动作、风险、所需授权和撤销条件 | 建议不是已执行结果；规则 Confidence=1 不是收益概率。 |

继续复用 DiagnosticEvaluator 的确定性事件、滞环、防抖和冷却；修复 unknown/freshness/实例时间后，再增加场景观察和建议。不要另写一套重复告警状态机，不把每个 threshold event 直接发给 Broker。[S10][][S11]

### 4.2 场景与收益指标分开设计

| **首批场景** | **需要补齐的最少证据** | **验收的目标指标** |
| --- | --- | --- |
| 用户标记的游戏/交互 | 前台 PID＋创建时间；场景保持；可选、短时帧时间或响应测量 | P95/P99 帧时间或响应延迟；缺少这些证据就不承诺提升。 |
| 明确的编译/渲染任务 | 任务标识、开始/完成和负载阶段；CPU/内存/I/O 相关性 | 相同输入的完成时间及吞吐；不以 CPU 使用率下降为成功。 |
| 电池上的后台工作 | 可靠供电状态、用户策略、可控制的非关键任务 | 同等任务完成量下的能耗/续航代价与前台响应。 |
| 内存压力排查 | 提交量/限额、有效缺页信号、进程私有内存与磁盘活动 | 压力缓解、交互/任务指标；不能只统计“释放了多少 MB”。 |

网络吞吐不是网络时延，磁盘吞吐不是 I/O 等待，GPU 所有 load 传感器最大值不是前台游戏 GPU 引擎利用率。新增更精细指标时使用新 ID，并明确硬件/权限不支持的质量状态。[S03][][S17]

### 4.3 “不动作”必须是一种正常决策

要求依赖指标新鲜、覆盖足够、目标身份明确并持续满足条件；证据不足则输出 unknown/建议补充观察。进入和退出使用不同门限，并有最小保持时间、冷却与资源预算；参数按回放与真实场景标定，不填写未经验证的通用“最优阈值”。

大模型不进入实时调参链路。后续本机基线学习应先离线评估误报、漏报和弃权率；模型只提出受限候选，最终动作仍经过现有确定性策略和 Broker 验证。

## 5 自动优化：复用 Broker，补齐恢复闭环

### 5.1 自动执行的必要顺序

> **动作生命周期**
>
> 场景/诊断 → 候选计划 → 用户与机器策略 → 重新校验目标 → 捕获原状态 → 持久化原状态和租约 → 执行 → 记录实际结果 → 限时评估 → 保留至期限 / 安全恢复 / 标记不确定并核对。

现有 BrokerActionCoordinator 已有身份、策略、幂等和 pending 审计，必须复用。新增的是可撤销动作所需的预写原值、归属、租约及冲突核对，而不是另建一个通用分布式事务系统。[S14]

| **恢复记录字段** | **用途与约束** |
| --- | --- |
| 目标实例 / 资源标识 | 进程动作使用 PID＋创建时间；机器级资源使用固定类型化标识，禁止任意路径/命令。 |
| original / applied / result | 分别表示修改前状态、期望设置和实际执行结果；不能只记录 requested。 |
| 策略版本 / 授权范围 / 到期时间 | 说明谁决定、允许改什么、保持多久；用户撤销和场景退出也可结束租约。 |
| 恢复状态 / 冲突原因 | 恢复前验证当前值仍为本软件应用值；用户或其他程序改过时不覆盖其新选择。 |
| 不确定状态 | Broker/Agent 崩溃或超时后先核对实际状态，不自动重复原动作，不伪造成功。 |

### 5.2 首批动作只做窄范围、可撤销控制

先做软件自身降采样/停用可选采集，以及用户明确授权的非关键后台任务调节；是否能用普通权限执行，应由具体 API/目标权限验证决定。真正需要特权的动作继续通过唯一 Broker 白名单，不扩张 Agent 权限。

EcoQoS 可作为未来新增的类型化候选，但它服务于非前台体验关键工作，并非对游戏或所有应用的通用加速。必须补能力检查、原状态捕获、恢复和对照试验后才接入，不能把它写成当前已支持的动作。[S28]

禁止默认自动结束进程、使用实时优先级、关闭安全保护、关闭页面文件、普遍清理工作集、执行任意注册表/脚本或强制最高功耗。现有 terminate_process 能力继续保持显式授权的独立行为，不纳入“可撤销优化”承诺。[S03][][S14]

### 5.3 冲突与收益判定

同一资源只允许一个有效自动控制者；用户手动设置优先于自动策略。多场景候选在 Actions 内做明确优先级仲裁，避免游戏、省电和后台任务策略互相覆盖。执行成功只说明 API 生效，不说明性能改善；收益需另行观察与对照。

建议模式 → dry-run → 单类动作 opt-in → 小范围自动化逐级开启。任何阶段出现目标身份不确定、审计不可写、恢复冲突或明显退化，都停止该资源上的新动作并显示原因。

## 6 可扩展性与 Desktop 产品化

### 6.1 以现有层级为边界

| **现有项目** | **应负责** | **不应负责** |
| --- | --- | --- |
| Contracts / Core | 公开类型、单位/身份/质量定义；调度与快照规则 | UI 控件、SQL 语句、特权具体执行。 |
| Collectors.Windows / Worker | 具体观测及能力、成本声明；故障隔离 | 场景策略或直接优化其他程序。 |
| Diagnostics | 证据窗、场景、瓶颈解释和建议 | 直接 P/Invoke 或向 Broker 发送未经过策略的动作。 |
| Actions / Broker | 计划仲裁、租约/审计模型；受控执行 | 任意插件命令、任意 shell 或任意注册表路径。 |
| Storage / Ipc | 版本化持久化与有界传输 | 改变指标语义或反压破坏基础采集。 |
| Desktop | 展示质量、历史、证据、用户授权与恢复状态 | 自己采集同一套指标、直接读库或直接连 Broker。 |

上述项目已经存在，本表约束的是职责而非建议新增工程。特别要避免又在 Desktop 内建立一套进程扫描或诊断判断。[S26][][S04][][S13][][S15]

### 6.2 扩展接入的最小合同

新增 Provider 声明 Group/Provider ID、周期、超时、权限、成本及结果质量；指标元数据集中登记单位、来源和是否允许持久化。新增规则声明所依赖指标与质量要求；新增 UI 只消费已有 contract/capabilities。不要为所有新功能增加主循环分支。

第一阶段仅支持受代码审查的第一方模块。出现明确第三方需求后，再在既有 Worker 协议上增加版本/能力协商、输入输出上限和生命周期控制；默认只读、按需启动、独立停用。Worker 的 250 ms 合并响应已有实现，应复用而不是再建第二个缓存层。[S16]

### 6.3 UI 按需求增量，而不是框架替换

先实施“最新状态槽＋至多一个 Dispatcher 回调”；窗口最小化/隐藏时暂停可视渲染，恢复时取最新快照。再在 Desktop 内提取小型展示模型，将控件布局、格式化、连接状态与用户命令分开，不要求引入新的 UI 框架。[S12][][S13][][S27]

功能顺序为：逐组质量与原因 → 进程实例详情 → 真实历史曲线和缺口 → 诊断证据与反证 → 建议/dry-run/授权 → 动作和恢复记录。所有按钮只调用 Agent；“数据不可用”与“当前没有问题”必须显示为不同状态。

IPC 订阅支持受控展示投影时，需要协商能力并兼容旧客户端；不能因 UI 少显示几项就停止 Agent 中被其他规则依赖的观测。

## 7 验证计划：准确性、干扰与动作收益分开

| **测试组** | **关键场景** | **验收证据** |
| --- | --- | --- |
| T01 契约/单位 | 原 fixtures、未知新增字段、null、整机/单核 CPU、进程身份、未来 JS 客户端整数无损往返 | Schema 与跨语言测试；旧 metricId 语义未改；不以占用正常替代口径校验。 |
| T02 时间/质量 | 时钟前后跳、睡眠恢复、实例切换、初始采样、部分覆盖、进程退出/PID 复用 | 无伪造 0/恢复事件；过期依赖不授权动作；离线回放稳定。 |
| T03 隔离/积压 | 多个不合作 Provider、Worker 崩溃、慢 IPC 客户端、UI 线程长阻塞、写库慢/不可写 | 基础组继续更新；所有队列按数量和字节有界；超时/丢弃原因可查询。 |
| T04 存储/隐私 | 完整数据库检查、持久化投影、迁移失败、WAL 崩溃、trace 到期/删除 | 实际落盘内容与声明相符；回放一致；历史不被静默丢失。 |
| T05 动作/恢复 | dry-run、真实允许动作、拒绝访问、身份变化、执行中崩溃、原状态写入失败、用户手动改值 | 拒绝条件可靠；不重复未知动作；只恢复仍归本软件控制的状态。 |
| T06 资源/稳定 | 各产品模式，100/500/1000 进程，启停/重连，最终候选真实 72 小时 | 全进程集合 CPU/内存/句柄/线程/队列/DB/WAL 原始曲线及窗口统计。 |
| T07 任务收益 | 固定游戏/交互、编译、渲染和后台工作负载，重复或交替对照 | 完成时间/帧时间尾部/响应/能耗；给出噪声区间，分清动作收益与场景变化。 |
| T08 部署/升级 | 项目四目标 x64，同 MSI 的安装/升级/回滚/卸载；Core 与可选 Broker | 签名、哈希、系统 build、服务身份、ACL、数据保留和 artifact 来源证明。 |

### 对照试验的三种状态

A：不运行 PerfMonitor；B：只运行监控，不动作；C：相同监控模式加一个已授权策略。A/B 测自身干扰，B/C 测策略增益。固定任务输入、设备、供电和软件版本，并控制暖机/缓存状态；不能把高负载阶段自然结束误认为优化成功。

每条自动策略分别验证“有收益的条件”和“不得启用的条件”。收益不显著但有额外开销的策略不进入默认自动集合；以人工批准的用户目标衡量，不统一使用 CPU 降低或内存释放量。

### 每份证据必须可追溯

记录 source commit、各产物 SHA-256、模式/参数、操作系统 build、硬件和供电状态、起止 UTC、单调经过时间、采样覆盖数/缺口、原始样本与统计脚本版本。远程 CI 摘要、虚拟时间测试、短时 smoke 与真实长稳证据分别命名。[S19][][S22]

## 8 里程碑出口与发布边界

| **里程碑** | **入口与工作范围** | **必须满足的出口** |
| --- | --- | --- |
| M0 基线冻结 | 固定当前 SHA；收集可重复环境和现有 CI 元数据 | 形成可重跑的资源测量及场景基线；明确最低设备、启用模式、优先场景。 |
| M1 质量＋轻量化 | WP-01～05 的核心部分；保留旧功能与契约 | T01～04、T06 的对应回归通过；资源实测和落盘检查通过；自动动作仍关闭。 |
| M2 场景建议 | WP-06 与证据展示，先覆盖少数明确应用场景 | 回放/真实对照可复现；正常高负载不过度干预；证据不足能弃权。 |
| M3 受控自动优化 | WP-07、08；只允许已批准的可撤销动作 | T05/T07 通过；原状态耐久化、异常恢复和用户修改冲突验证通过；按范围 opt-in。 |
| M4 扩展与自适应 | WP-09；由明确扩展场景触发 | 新模块独立故障降级、不改主采样循环；扩展资源预算和兼容性门禁通过。 |

### 生产发布是独立门禁

当前精确 SHA 的 Windows CI 成功，但可见步骤含虚拟 72 小时和加速资源测试；它们不能替代真实 72 小时，也不能替代 Windows 11 客户端安装矩阵。该 run 当前没有可读取 artifact，本次没有取得原始资源数值。[S21][][S22][][S23]

保留已有 v0.4 基线、签名、SBOM、漏洞扫描和支持矩阵门禁，并新增“最终候选全产品集合”长稳证据。不要删除原门禁以快速合并，也不能用祖先版本的好结果代替当前 Worker/存储/动作代码的验证。[S19][][S30]

项目声明的支持目标为 Windows 11 24H2/25H2 x64 与 Windows Server 2022/2025 Desktop Experience x64。实际产品支持承诺依赖完整证据；本规划不扩大到 Windows 10、ARM64 或 Server Core。[S20]

### 失败与回退

监控退化时优先停用新增可选 Provider/诊断规则，保留基础指标；UI 失败不影响 Agent。存储升级前保留可验证备份，旧程序不能写不认识的新 schema。自动策略失败先停止续租并按拥有关系恢复，存在 indeterminate 时先核对实际状态。

每个工作包交付独立、可审查的差异；不可逆变更或破坏 contract v1 的需求必须单独决策。没有生产签名证书、完整支持矩阵或发布审批时，只能停留在开发/测试候选，不称为正式发布。[S19]

## 9 需求追踪、责任与完成定义

| **用户目标 / 需求** | **关联发现** | **工作包** | **验收** |
| --- | --- | --- | --- |
| R1 后台开销受控 | F01/F02/F05/F10 | WP-00～04 | T03/T04/T06；按整个产品进程集合统计。 |
| R2 界面不拖慢电脑 | F06 | WP-05 | T03/T06；UI 队列有界，隐藏恢复正确。 |
| R3 智能识别不同情况 | F03/F04/F07 | WP-03/06 | T02/T07；有质量约束和反证，不把高占用当作故障。 |
| R4 优化有效且可恢复 | F08 | WP-07/08 | T05/T07；授权、执行、原状态、效果和恢复可追溯。 |
| R5 层级解耦与扩展 | F09 | WP-01/05/09 | T01/T03；稳定接口、故障局部降级、动作白名单独立。 |
| R6 可交付和长期维护 | F10 | 全部；M0/M1 发布证据 | T01～08；源码、产物、环境和原始结果关联。 |

### 责任分工

Core/.NET 负责人掌握采样、快照和预算；存储负责人掌握投影、迁移及回放；诊断负责人掌握规则和场景证据；Desktop 负责人只负责展示与用户意图；Broker/安全审查者复核授权、目标和恢复边界；Windows 测试负责人执行真实场景、故障注入和长稳。团队较小时可合并角色，但真实动作的实现与复核不应省略。

### M0 需要确定但不阻塞独立修正的输入

最低支持设备与进程规模、优先支持的两三个应用场景、用户愿意授权的动作类别、硬件采集默认是否启用、历史保留/隐私要求、签名与测试主机条件。参数不明时继续完成无输出短路、缺测不填 0、质量展示和持久化范围校正；不代填硬件极限或收益百分比。

### 每个工作包的完成定义

实际修改文件和相对本次固定 SHA 的差异；必要参数与接入说明；新增/旧功能的实际测试命令与结果；原始资源证据；失败与未验证范围；可操作的回退办法。只提供方案或示例不算实现完成，主机桩测试不能写成 Windows 实机验证。

> **下一轮开发的最小完整范围**
>
> 启动 WP-00，同时完成 WP-03 的缺测/时间语义，WP-01 的输出短路及快照开销收敛，WP-02 的持久化边界修正。上述路径有证据后再进入场景建议；在原状态耐久化与恢复测试完成前，维持自动系统修改关闭。

## 附录 证据索引

仓库代码链接均固定到本报告基准提交；GitHub 分支、CI 与 artifact 列表为核查时的远程状态。引用编号可直接点击。

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

[\[S16\] Collectors.Worker/HardwareWorkerCoordinator.cs][S16]

单客户端串行化、250 ms 响应复用和引用计数释放。

[\[S17\] Collectors.Worker/HardwareWorkerProviders.cs][S17]

GPU/温度 5 秒周期；devices 明细进入快照；load/temperature 最大值口径。

## 附录 证据索引（续）

仓库代码链接均固定到本报告基准提交；GitHub 分支、CI 与 artifact 列表为核查时的远程状态。引用编号可直接点击。

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

[\[S25\] Storage.Sqlite/SqliteHistoryOptions.cs][S25]

队列 256、批上限 64、busy_timeout 1 秒；raw 48 小时、分钟 30 天、小时 366 天。

[\[S26\] src 项目目录][S26]

16 个现有项目；模块与进程边界均已存在。

[\[S27\] Microsoft：WPF Threading model][S27]

Dispatcher 排队与 UI 线程执行模型；核查日 2026-10-02。

[\[S28\] Microsoft：SetProcessInformation][S28]

EcoQoS 适用于非前台体验关键工作，并非前台性能通用加速开关。

[\[S29\] SQLite：PRAGMA synchronous][S29]

WAL＋NORMAL 的一致性与断电持久性边界；不能将进程崩溃测试等同断电验证。

[\[S30\] release/evidence 目录][S30]

当前树可见 v0.4-agent-baseline-candidate.json；本次未取得真实 72 小时结果数据。

  [S03]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/README.md
  [S19]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/docs/architecture/v1.0-release-architecture.md
  [S26]: https://github.com/Wanchenrui/windows-performance-monitor/tree/74d29ddfc8a29fac23af5d57068b2aef08c42804/src
  [S04]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Agent/AgentServiceRunner.cs
  [S05]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Core/SnapshotAssembler.cs
  [S06]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Core/ProviderScheduler.cs
  [S08]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Storage.Sqlite/SqliteHistoryStore.cs
  [S10]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Diagnostics/DiagnosticSnapshotReader.cs
  [S13]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Desktop/MainWindow.cs
  [S14]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Actions/ActionExecution.cs
  [S18]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/release/resource-budgets-v1.json
  [S07]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Collectors.Windows/ProcessProvider.cs
  [S17]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Collectors.Worker/HardwareWorkerProviders.cs
  [S09]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Storage.Sqlite/SnapshotMetricExtractor.cs
  [S25]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Storage.Sqlite/SqliteHistoryOptions.cs
  [S29]: https://www.sqlite.org/pragma.html#pragma_synchronous
  [S11]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Diagnostics/DiagnosticEvaluator.cs
  [S28]: https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation
  [S15]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Agent/AgentQueryService.cs
  [S16]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Collectors.Worker/HardwareWorkerCoordinator.cs
  [S12]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/src/PerfMonitor.Desktop/DesktopAgentSession.cs
  [S27]: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model
  [S22]: https://api.github.com/repos/Wanchenrui/windows-performance-monitor/actions/runs/30562783265/jobs?per_page=100
  [S21]: https://github.com/Wanchenrui/windows-performance-monitor/actions/runs/30562783265
  [S23]: https://api.github.com/repos/Wanchenrui/windows-performance-monitor/actions/runs/30562783265/artifacts?per_page=100
  [S30]: https://github.com/Wanchenrui/windows-performance-monitor/tree/74d29ddfc8a29fac23af5d57068b2aef08c42804/release/evidence
  [S20]: https://github.com/Wanchenrui/windows-performance-monitor/blob/74d29ddfc8a29fac23af5d57068b2aef08c42804/docs/support-matrix-v1.md
