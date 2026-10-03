# Wave03：快照路径与调度隔离的开发测量

起始锚点为 2026-10-02，WP01 最后样本实际于 2026-10-03 00:07:11
（Asia/Shanghai；UTC 为 2026-10-02 16:07:11）完成。版本保持 `1.1.0`
开发候选，基线 HEAD 为 `73ec33fd5c27e2658d5ce2c514388b71eb9a406b`。
本轮没有发布、tag、服务/Broker 启动、特权动作或前台输入验收。已有报告、
Wave02 evidence、数据库和构建原件不覆盖。

## WP01：先复现分配，再冻结载荷

旧 SnapshotAssembler 每次发布先克隆结果，再重建并克隆所有组；Read
还在锁内重建所有 JsonNode 和 sampler 数据。新增独立开发工具
`tools/PerfMonitor.SnapshotBench`，先保存改前样本再修改产品代码。
它使用确定生成的 100/500/1000 行进程数组，不枚举真实进程。四个
cohort 都没有运行 Agent、Desktop、IPC、SQLite I/O、硬件 Worker 或
Broker；这些是单进程快照微测量，不能称为真实进程规模、全产品基线、
预算达标或用户任务收益。

产品差异只涉及 SnapshotAssembler、新增 SnapshotGroupJsonConverter
及一个已确认热路径 DiagnosticProcessProjection。SnapshotGroup 在边界
冻结 JsonElement、errors 和 coverage reasons；AgentSnapshot 拥有只读
groups 副本。未变化组及其载荷复用；Read 捕获一个原子版本，然后仅
投影该版本的年龄和新鲜度。UTC、单调时间、sequence、observation
identity/time、delivery、质量和 held value 语义保留。当前 Health 继续
使用原 Read 接口，已不因 envelope 读取展开进程树，没有另建健康服务。

`Data` 仍提供 JsonNode 外观，每次取得独立、惰性展开的视图。原地修改
只影响该视图；需要替换组时显式使用 `group with { Data = view }`。
消费者应取一次 Data 并复用局部变量。`ReadOnlyData` 是有效期由组拥有
的不可变 JsonElement 入口，采用 JSON 原生的精确键名查找。本轮仅迁移
关注进程投影到该入口，存储/诊断仍复用同一投影，UI 未做模型迁移。
序列化直接写冻结元素；v1 字段、标量含义和持久化接口未扩张。

旧 in-memory DTO 容许非有限数值，让消费者判缺测；第一次冻结实现使
已有 NaN 展示测试在构造时抛错。最终实现只在序列化抛 ArgumentException
且可确认存在直接 double/float NaN/Infinity 时建兼容 fallback：私有原
数据拥有深副本，Data 独立视图保留非法值；ReadOnlyData 把这些数值
表示为 null，原始非法载荷的 wire 序列化仍拒绝。其他异常，包括与 NaN
混合的失败值，继续传播。正常 JSON 快路径仍只冻结一次；本轮不能把
非有限值当成 0 或有效观测。

## WP01：样本、统计与可追溯范围

每个 cohort 为 3 试次，每个试次/行数/操作先暖机 100 次，再取 1000
测样，合计 81 场景、81,000 原样本。每场景独立 assembler，试次轮换
操作顺序。固定 synthetic TimeProvider 保持新鲜度稳定，时间跳变、
观测身份和并发原子性由独立测试覆盖。Stopwatch 计时包括委托和计时器
成本；timer-control 只报告，不从其他值扣减。当前线程分配计数仅覆盖
同步操作，fixture、暖机及 CSV/summary 输出在计数外；publish 操作的
ProviderResult/小组数据构造也计入，改前/改后方法完全相同。

P95/P99 使用 nearest rank，即排序后取 `ceil(p*N)-1`，不删除异常值，
不强制 GC。摘要保留每个试次的 mean allocation、全部延迟分位、极值
和自然 GC 次数。下表是三试次统计值各自的中位数，不能称为把三个
试次合并后的 P95/P99。自然 JIT、GC、OS 和用户负载没有被控制，特别
是早期 100 行试次存在暖机/分层编译影响；短串行样本不证明显著性。

| 本地 cohort 原件 | 用途 | CSV 字节 / 测样数 | 单调测量总秒数 |
| --- | --- | --- | --- |
| `artifacts/wave03-wp01-before-samples` | 旧快照路径；产品改动前测量 | 4,083,356 / 81,000 | 46.040864 |
| `artifacts/wave03-wp01-after-samples` | v1 冻结视图；暴露三遍展开退化 | 3,939,390 / 81,000 | 30.562520 |
| `artifacts/wave03-wp01-after-v2-samples` | 窄只读入口与生产关注投影；NaN 修复前 | 3,917,889 / 81,000 | 29.478599 |
| `artifacts/wave03-wp01-final-samples` | 最后快照实现，含非有限值兼容封装 | 3,918,858 / 81,000 | 30.108041 |

每个目录的 summary.json 含原 CSV SHA-256、source HEAD/dirty、相关
source SHA-256、实际 DLL/runtime SHA-256、输入和初始快照 SHA-256、
OS/runtime/架构、28 逻辑处理器、UTC 与单调经过时间。C# harness 的
源码哈希四组相同；三个合成输入及其初始快照的原始 JSON 字节哈希
四组相同，包括 64 位进程创建时间。改前到最后 cohort 的相关源差异
只有 SnapshotAssembler、增加的 converter 和 DiagnosticProcessProjection。

以下是四组逐字节相同的输入证明；providerResultSha256 对工具确定生成
的进程 JsonArray 序列化 UTF-8 字节计算，initialSnapshotSha256 对初次
完整 AgentSnapshot 的同样字节计算。这里的大小是进程数组 JSON 大小，
不是 IPC 帧或数据库字节：

| 合成行数 / 输入 JSON 字节 | providerResultSha256 | initialSnapshotSha256 |
| --- | --- | --- |
| 100 / 45,081 | `8e6d71430ce00984cb65c92e06e4583c254189e964df69957a4d0b6ccc07ba6b` | `480e6199c8c8d12c7de1a5851d3674c7308b9c748779135a5ae6b463c962ad94` |
| 500 / 225,401 | `eac3d3356ee0e1b59dd1b58939c7ef2eea6678934cf80a5cd571980a9eba5c7c` | `be940ce01add604bb1184dc1459c204a8d9308d080b22971003d1e09f1280d4d` |
| 1000 / 450,801 | `50bcd7b4aa65c69bbda32b62d1cf9cc576bafb9ed5a84d1e18797578ea22dea1` | `6413639b0554766efb054803e15921da1fed36a3364c930576408bfc66d6377f` |

| WP01 最后候选相关源文件 | SHA-256 |
| --- | --- |
| `src/PerfMonitor.Core/SnapshotAssembler.cs` | `f4dc183bd7b4a396b098713a584ccfee1a78d27b3b0a057682e3a180c462243d` |
| `src/PerfMonitor.Core/SnapshotGroupJsonConverter.cs` | `7a210eeaeb8debee46b6df6e52965ff63ca243c51f92d3f0a6af7f9db026b498` |
| `src/PerfMonitor.Diagnostics/DiagnosticProcessProjection.cs` | `ee1702eb08edef180aac5978433538d82b6632baad28e61e1ad038d5f006bd94` |

改前的 SnapshotAssembler 和 DiagnosticProcessProjection 源哈希分别为
`72521607c97a9a8e1907ab196bea9a80ad433e07ec64f4a5e76751b500da3986`
和 `a54500a4cb37599d45a09ca27f6586bc49931353bac1f1d3941d8a0926ce13ca`；
旧树没有 converter。四组 C# harness 原始哈希均为
`c8396605cd8e6f3d5cbc513ae2ea4f9fba8c8f01e99b77eaf83e4d3418dac51b`。

所有原 CSV 留本地；必要 summary、验证日志及复算可归档。四组逐样本
的独立 Python 重算结果为
`artifacts/wave03-wp01-validation/independent-recalculation-final.json`：
324,000 原样本、324 场景的索引/数目、ticks/微秒、分配、均值、极值和
分位与摘要相符，同时核对输入、初始快照及 C# harness 哈希。此前三组
重算原件 `independent-recalculation.json` 也保留。复算器为
`tools/PerfMonitor.SnapshotBench/check_results.py`，只使用 Python 标准库。

这个“final”只表示最后的 WP01 快照实现候选。上述四份 Core cohort
同时包含早期 WP04 scheduler，但微工具没有构造或运行调度器。后续
交叉审查发现 scheduler_capacity_timeout 不属于冻结 v1 errorCode enum，
它必须复用已有 timeout 后才进入最后统一验证。微测量不因一个未执行
调度路径修正而冒称已绑定最终全树；修正前后的源输入差异需与统一验证
一同核对。WP01 的实现文件哈希及 synthetic 输入可以单独关联。
四组微测量中 ProviderScheduler 源哈希相同，均为
`475d170a949c0ef5614318e082bc7685486ebe3c01a71f5a88c4e2d051e8d989`；
复用 timeout 修正后的该文件为
`46281574721bef37fb18be55fb6b3beffd00439b3f850be6bb789a6571796e9e`。
这项差异未执行在快照微工具中；三个 WP01 文件和 C# 微工具在修正后
仍与最后微 cohort 哈希一致。

## WP01：所有九条路径的改前/最后测量

每格为“改前 / 最后”，分配为 byte/op，P95/P99 为微秒。payload-once
遍历所有行一次；payload-three 在一个 Read 后独立取 Data 并完整遍历
三次；watched-projection 调用真实关注进程投影，固定两个名字。

| 合成行数 | 操作 | 平均分配 byte/op | P95 us | P99 us |
| --- | --- | --- | --- | --- |
| 100 | `timer-control` | 0.0 / 0.0 | 0.1 / 0.1 | 0.1 / 0.1 |
| 100 | `read-envelope` | 254,696.0 / 248.0 | 110.9 / 0.2 | 258.3 / 1.1 |
| 100 | `read-payload-once` | 254,784.0 / 250,056.0 | 113.5 / 158.5 | 294.8 / 352.8 |
| 100 | `read-payload-three` | 254,960.0 / 749,672.0 | 149.0 / 407.6 | 293.5 / 597.5 |
| 100 | `serialize-existing` | 95,160.0 / 96,952.0 | 311.8 / 270.1 | 1,309.2 / 702.2 |
| 100 | `read-serialize` | 349,856.0 / 97,200.0 | 249.7 / 201.7 | 2,659.6 / 642.2 |
| 100 | `watched-projection` | 261,024.0 / 17,472.0 | 122.1 / 73.3 | 251.5 / 123.9 |
| 100 | `publish-small-held` | 256,832.0 / 7,536.0 | 98.1 / 10.3 | 431.8 / 37.5 |
| 100 | `publish-process` | 503,040.0 / 103,032.0 | 189.5 / 238.4 | 694.9 / 280.5 |
| 500 | `timer-control` | 0.0 / 0.0 | 0.1 / 0.1 | 0.1 / 0.1 |
| 500 | `read-envelope` | 1,246,504.2 / 248.0 | 602.2 / 0.2 | 1,027.5 / 0.6 |
| 500 | `read-payload-once` | 1,246,784.0 / 1,248,456.0 | 646.2 / 785.8 | 1,054.1 / 1,248.3 |
| 500 | `read-payload-three` | 1,246,960.0 / 3,744,872.0 | 718.1 / 1,898.8 | 1,070.9 / 2,261.0 |
| 500 | `serialize-existing` | 455,907.0 / 458,135.3 | 333.3 / 347.3 | 450.2 / 458.1 |
| 500 | `read-serialize` | 1,702,497.2 / 458,342.6 | 4,053.6 / 362.1 | 16,047.1 / 473.9 |
| 500 | `watched-projection` | 1,253,024.0 / 39,872.0 | 603.7 / 35.9 | 1,105.9 / 41.5 |
| 500 | `publish-small-held` | 1,248,832.0 / 7,496.0 | 1,331.5 / 3.0 | 1,978.9 / 3.5 |
| 500 | `publish-process` | 2,487,040.0 / 489,737.0 | 3,142.5 / 625.4 | 3,874.0 / 748.8 |
| 1000 | `timer-control` | 0.0 / 0.0 | 0.1 / 0.1 | 0.1 / 0.1 |
| 1000 | `read-envelope` | 2,486,504.0 / 248.0 | 1,685.8 / 0.2 | 2,732.7 / 1.0 |
| 1000 | `read-payload-once` | 2,486,784.0 / 2,496,456.0 | 1,709.8 / 1,798.3 | 2,699.4 / 2,990.3 |
| 1000 | `read-payload-three` | 2,486,960.0 / 7,488,872.0 | 1,921.6 / 4,103.1 | 3,144.1 / 5,619.9 |
| 1000 | `serialize-existing` | 906,715.7 / 908,931.8 | 704.0 / 696.9 | 973.1 / 841.8 |
| 1000 | `read-serialize` | 3,393,305.1 / 909,169.7 | 4,618.6 / 689.9 | 21,729.9 / 892.2 |
| 1000 | `watched-projection` | 2,493,024.0 / 67,872.0 | 1,703.8 / 72.6 | 2,625.7 / 132.2 |
| 1000 | `publish-small-held` | 2,488,832.0 / 7,496.0 | 3,516.2 / 3.1 | 4,686.3 / 3.7 |
| 1000 | `publish-process` | 4,967,040.0 / 973,221.0 | 6,708.7 / 1,217.0 | 7,391.2 / 1,361.9 |

读 envelope 与 held 组发布已不随进程行数复制整棵树；关注进程的真实
投影路径通过只读入口减少了展开分配。不能把这三项直接外推为产品
CPU、UI 或任务收益。三次独立可变视图全展开约产生三倍分配和更差
P95/P99；单次完整展开的分配近持平，尾延迟也没有普遍改善。100 行
process publish 的 P95 从 189.5us 升到 238.4us，P99 则降低；500 行
纯序列化 P95 从 333.3us 升到 347.3us，分配增加约 2.2KB。已如实保留
这些成本，没有把开销移到消费者后只报告 envelope 收益。有效快路径
兼容封装相比 v2 每次 publish 额外约 48B，关注投影额外约 24B。

## WP01：实际验证、复现与剩余出口

全部构建使用隔离 SDK `10.0.302`，各新目录分别 locked restore、Release
build，0 警告/错误。工具 self-check 校验 nearest rank 与微秒换算后
才采样。最终针对快照、存储投影/迁移、诊断时间/回放、IPC、展示与
趋势的定向回归 81/81，零失败/跳过；后续混合异常两个排列追加断言后，
ownership 专用 10/10；ContractTests 11/11。它们不构成最后统一全量
回归或物理 Desktop 操作验收。

初次定向 78/79 的 NaN 兼容失败保存在
`artifacts/wave03-wp01-tests/validation/targeted-tests.log` 和原 TRX。
第一次异常测试替身依赖 JsonValue 自定义 type-info，但写出时默认 options
未使用该 converter，80/81 的失败为新测试替身缺陷，原件
`targeted-fixed-tests.log` 也保留。替身改为直接抛错的属性，修复后
`targeted-final-tests.log` 记录 81/81；`ownership-final-tests.log` 记录
10/10。测试没有将 NaN 当有效数值，也没有通过改掉原展示断言隐藏回归。
专用用例覆盖 provider/consumer 输入修改、groups/errors/reasons 别名、
with 赋值、所有 JSON 根形状、64 位 identity、序列化/反序列化、旧
元素生命周期、held/stale 组、并发版本及容量 null 观测；原存储敏感
fixture 仅改为先取视图、添加敏感字段、再 with 赋回，以保留真实测试输入。

各 build/measurement 日志在 `artifacts/wave03-wp01-*-build/validation`，
定向 TRX 在 `artifacts/wave03-wp01-tests/test-results`，契约 TRX 在
`artifacts/wave03-wp01-contracts/test-results`。交叉审查另外阻断了 WP04
新错误码不符 v1 enum 的问题；最终 schema 修正/统一回归使用新 cohort，
不覆盖这组微测量或中间失败原件。

重跑当前候选时使用两个尚不存在的新目录，先协调独占采样窗口。构建
和数据收集分开，不用 dotnet run 混合编译和计时：

```powershell
$sdk = 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302\dotnet.exe'
& $sdk restore tools/PerfMonitor.SnapshotBench/PerfMonitor.SnapshotBench.csproj --locked-mode --artifacts-path artifacts/wave03-wp01-reproduce-build
& $sdk build tools/PerfMonitor.SnapshotBench/PerfMonitor.SnapshotBench.csproj -c Release --no-restore --disable-build-servers --artifacts-path artifacts/wave03-wp01-reproduce-build
& $sdk artifacts/wave03-wp01-reproduce-build/bin/PerfMonitor.SnapshotBench/release/PerfMonitor.SnapshotBench.dll --self-check
& $sdk artifacts/wave03-wp01-reproduce-build/bin/PerfMonitor.SnapshotBench/release/PerfMonitor.SnapshotBench.dll --repository D:\GitHub\windows-performance-monitor --output D:\GitHub\windows-performance-monitor\artifacts\wave03-wp01-reproduce-samples --label reproduce --iterations 1000 --warmup 100 --trials 3
& 'C:\Users\Administrator\AppData\Local\PerfMonitor\venv-3.12.10\Scripts\python.exe' tools/PerfMonitor.SnapshotBench/check_results.py artifacts/wave03-wp01-reproduce-samples --output artifacts/wave03-wp01-reproduce-check.json
```

历史旧样本只能由其记录的源哈希与保存的 DLL关联；当前已修改的源树
重跑不等于旧源码。观察到的调用组合改善仍需后续真实 Agent/Worker、
无 UI/隐藏/可见 UI、慢 IPC 客户端、真实 100/500/1000 进程规模、锁
竞争、全进程资源和任务对照验证；72 小时与完整平台矩阵未关闭。这个
微工具不测真实 IPC 字节、数据库增长、跨进程反序列化或系统唤醒。
回退 WP01 时整体恢复上述三个实现文件；消费者显式 with 赋值仍兼容
旧 DTO，v1 schema、metric ID、版本和持久化接口无需回退迁移。

## WP04：先复现共享槽耗尽，再做最小隔离

本轮只完成 WP04 中的 F05 调度隔离，仍是 `1.1.0` 开发候选。产品进程
集合 Self Metrics、全产品预算、72 小时长稳及平台矩阵不由这个合成夹具
验收。基线 HEAD 为 `73ec33fd5c27e2658d5ce2c514388b71eb9a406b`，每组
样本绑定冻结输入、实际 DLL、SDK、命令参数及 SHA-256。

旧 scheduler 在采集超时后保留未结束调用的共享槽，避免无界并发，但
等待共享槽没有期限。夹具先注册 5 个同步阻塞且忽略取消的 Provider，
再注册 systemCpu、memory、network、diskIo 四个立即成功的基础组。
总槽固定为 5；阻塞组周期 50ms、超时 40ms，基础组周期 100ms、超时
250ms。槽位饱和后请求观察 3 秒，预设进度预算为成功发布最大间隔
不超过 250ms。最大间隔包含窗口两端；0 样本的缺口等于整个窗口。
夹具在 Stop 后显式释放同步阻塞，不使用真实硬件采集器或永久原生调用。

最早的 `artifacts/wave03-wp04-before` 只有 locked restore/Release build；
wrapper 的解析错误发生于子进程启动前，只是无效工具自检，未给出数值。
修正工具后的 `wave03-wp04-before-v2/run` 首先量化故障：四个基础组均
0 成功，最大缺口 3076.4178ms，总调用峰值 5，每 Provider 同时峰值 1。
实测旧故障完成后才实施本轮调度改动。

## WP04：实现边界与契约阻断修复

总并发上限保持原值。启用基础保留名单且总槽大于 1 时，可选调用先拿
容量为 `maxConcurrency - 1` 的可选门，再拿原总槽；可选门排队不会
预占总槽。AgentRunner/AgentServiceRunner 配置既有稳定组标识 systemCpu、
memory、network、diskIo、uptime、self。默认总槽 5 时，可选调用最多
长期占用 4 个槽，剩余容量供健康基础组使用。基础组也能使用其余空闲槽。
没有新增线程、Worker、线程池或通用队列框架。

两个容量门共用 descriptor.Timeout 的排队期限。未拿到容量时发布
`availability=timeout`、`errorCode=timeout`、Data=null、ObservedAtUtc=null、
DurationMilliseconds=0、StartedTimestamp=null，记录实际完成 timestamp，
增加 skippedBusy，并沿用有界回退。状态发布时间推动快照及该组记录序号；
Provider 观测 elapsed 仍为空、freshness 为 warming_up，不制造新鲜成功
观测。原先活动 CPU 告警遇到该缺测也不能被当作低 CPU 恢复。采集自身
超时与排队期限分别计时，这不是端到端硬期限。

初版隔离候选曾使用 `scheduler_capacity_timeout`，交叉审查发现它违反
冻结 v1 schema 的封闭 errorCode enum。`wave03-wp04-after` 与旧
`wave03-wp04-targeted` 全部保留为 **contract-invalid 的早期候选**：其
调度事件原件仍能解释故障机制，但不能证明候选符合 v1。早期改后四组
各 31 次成功、最大间隔 CPU 110.7190 / memory 110.8000 / network
110.5402 / diskIo 110.6206ms；没有删掉这些样本或用新数据覆盖它们。
最终复用 `StableErrorCodes.Timeout`，不扩展 schema；容量等待通过空
观测时间、空开始 timestamp、零采集时长和已有 skippedBusy 区分。

## WP04：最终新配对、真实 schema 与资源记录

最终配对为 `wave03-wp04-before-v3` 和 `wave03-wp04-after-v2`。两组
共用新版 probe，新增真实容量结果捕获；完整快照序列化在计时、Stop、
fixture cleanup 后执行。为单独测调度变化，两组其余 Core/Contracts
依赖均沿用首个有效旧 cohort 冻结输入，唯一配对源码差异仍是
ProviderScheduler。原 3 秒请求、周期、超时、基础组集合及总槽一致；
仅改后启用基础容量保留。当前 WP01 合并实现另外由定向测试覆盖。

| 基础组 | 旧成功数 / 最大间隔 ms | 最终改后成功数 / 最大间隔 ms | 预设预算 ms |
| --- | --- | --- | --- |
| systemCpu | 0 / 3074.4277 | 31 / 110.8236 | 250 |
| memory | 0 / 3074.4277 | 31 / 110.6631 | 250 |
| network | 0 / 3074.4277 | 31 / 110.7142 | 250 |
| diskIo | 0 / 3074.4277 | 31 / 110.6866 | 250 |

两组总调用峰值均 5、每 Provider 峰值均 1。阻塞调用峰值 5→4；第五个
阻塞组等待容量，没有开始采集。Stop 耗时 1007.2020→1007.5060ms，
返回时仍有 5→4 个在途调用；调用结束前错误 Dispose 两组均 0，fixture
释放后两组活动调用均 0，全部 Provider 释放完成。超时后不会在正常
运行中释放未结束调用的容量；停止沿用原有限等待及延迟 Dispose。

真实捕获的 `probe.blocked.4` 容量结果在
`wave03-wp04-after-v2/run/capacity-failure.execution.json`，通过真实
SnapshotAssembler 写出的完整快照为同目录 `capacity-failure.snapshot.json`。
未采集的必需组只注册 descriptor 以保留 warming_up/null 形状。Python
Draft202012Validator + FormatChecker 对现有
`contracts/v1/snapshot-v1.schema.json` 验证完整快照；因为 synthetic 组
属于 additionalGroups，还直接用同一 schema 的 `$defs.baseGroup` 验证
该实际失败组，确保封闭错误枚举被检查。两项均通过。内存负控将错误码
改回初版字符串，枚举按预期拒绝；不改写真实采样。检查还覆盖空观测、
空开始 timestamp、零采集时长、skippedBusy=1、warming_up/null elapsed。
检查脚本、日志、结果及原件 hash 在 `after-v2/validation` 的
`validate_capacity_contract.py`、`schema-validation.log`、
`capacity-contract-validation.json`。

资源记录来自同一个后台控制台夹具，包含 JIT、ThreadPool、事件记录器和
观察者成本，未启动 Desktop、硬件 Worker、Broker、服务或特权采集。
两组均 29 行资源样本 / 28 个区间；CPU 有效覆盖 3.0671901→3.0612268s，
CPU 时间增量 0.234375→0.203125s，单核等效均值 7.641359→6.635412%，
28 逻辑处理器归一化均值 0.272906→0.236979%。私有内存峰值
13,611,008→13,717,504B，工作集峰值 33,030,144→34,050,048B，线程峰值
22→25、句柄峰值 264→271。短串行样本及环境负载/JIT 差异不能证明
CPU 优化收益，也不是 Agent 或全产品预算验收；本轮判断依赖基础组
进度及并发/生命周期记录。

## WP04：回归、复算、复现与剩余边界

新配对各自 locked restore 3 项目、Release build 0 警告 / 0 错误。当前
最终合并源码的 `SchedulerIsolationTests` 6 项、原 `SchedulerTests` 4 项、
stateful/deferred Dispose 2 项，再加 WP01 容量 ownership 回归，合计
13/13 通过、零失败/跳过。覆盖多个不合作调用、单槽兼容及恢复、两个
门等待取消、可选失败释放/回退、未知基础组、容量缺测与告警状态。
`wave03-wp04-targeted-v2/validation` 保存 tests.log、TRX、source-before
及 source-after-check；测试期间相关源码 hash 未变。

同目录 `recompute_scheduler_probe.py` 离线独立复算两组 CSV 与摘要，
核对各组成功数和含边缘最大 gap、CPU 时间小数增量/有效覆盖/归一化、
内存/线程/句柄峰值、逐 Provider 进入退出配对及非重入、所有冻结输入
hash，以及配对唯一源码差异。`probe-independent-recompute.json` 已
通过，重跑脚本拒绝覆盖既有结果。归档小文件清单及大小/hash 见
`wave03-wp04-targeted-v2/wp04-evidence-file-manifest-final.json`。

最终 scheduler SHA-256：
`46281574721BEF37FB18BE55FB6B3BEFFD00439B3F850BE6BB789A6571796E9E`。
新 probe SHA-256：
`E62032FD02C1D7503DDDD30DDA88BA69B5DCEA135C1AB0A82C975542858D9608`。
专用调度测试 SHA-256：
`E1625E579C48188083F4FDDD6B86E7378D0464233F3612ADD46457741D50B4D6`。
真实 schema SHA-256：
`FA8068A7C35CA1AC48609D46C4BAA2CE5301FF286A0017EE8F65EA80A55EA538`。
其余输入和实际 DLL hash 见两组 provenance/source-freeze，不将当前
工作树误称为旧源码。

后续统一 Python 门禁发现新增 wrapper 缺少仓库要求的 UTF-8 BOM。
最终脚本只补 3 字节 BOM，4041→4044B，不改正文或行为；去 BOM 后
内容 hash 仍等于测量时的
`24A14F751F79021CC28CBB6B29C55D488E47B4215468C983F222E7B53DBDFF81`。
当前脚本最终 hash 为
`441C4AAF4518DE7FFB5D16F66889B4DC271FC7E20092F137DB0362C77E6A1C60`。
`final-wrapper-encoding.json` 记录字节差异及绑定范围，冻结配对 source
和原测量不改写；统一 Python 全量使用新的验证 cohort 重跑。

复现必须协调独占 build/test/采样窗口，选择尚不存在的输出目录。历史
配对使用对应冻结 source；下面重跑最终改后，构建与运行分开：

```powershell
$sdk = 'C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302\dotnet.exe'
$freeze = 'D:\GitHub\windows-performance-monitor\artifacts\wave03-wp04-after-v2\source'
$build = 'D:\GitHub\windows-performance-monitor\artifacts\wave03-wp04-reproduce-build'
Push-Location $freeze
try {
  & $sdk restore scripts/scheduler_fault_probe/PerfMonitor.SchedulerFaultProbe.csproj --locked-mode --artifacts-path $build
  & $sdk build scripts/scheduler_fault_probe/PerfMonitor.SchedulerFaultProbe.csproj -c Release --no-restore --disable-build-servers --artifacts-path $build
  & .\scripts\run_scheduler_fault_probe.ps1 -DotnetPath $sdk -SourceRoot $freeze -ProbePath "$build\bin\PerfMonitor.SchedulerFaultProbe\release\PerfMonitor.SchedulerFaultProbe.dll" -OutputDirectory 'D:\GitHub\windows-performance-monitor\artifacts\wave03-wp04-reproduce-run' -DurationSeconds 3 -ReserveBasicGroups
}
finally { Pop-Location }
```

旧故障改用 `wave03-wp04-before-v3/source`、另一组新 build/run 目录并省略
`-ReserveBasicGroups`。wrapper 仅设置所属子进程 DOTNET_ROOT，后台执行
实际已构建 DLL，保存 stdout/stderr、源码/产物 hash 及退出记录。

maxConcurrency=1 保持共享单槽兼容性，有限容量等待能够报告缺测；这一
配置不能同时保留基础容量并允许可选采集前进，因而没有隔离保证。基础
组本身多个永久阻塞调用仍可能耗尽总容量，结果 sink 卡住或 ThreadPool/
OS 资源耗尽也可能延迟基础组。本证据只支持本夹具中健康基础采集面对
多个可选阻塞调用仍在预设预算内前进。永久原生调用终止、真实硬件/
Worker 故障、硬实时、全产品资源预算和 72 小时长稳仍未验收，停止后的
永久在途调用依靠现有进程边界回收。

回退可移除两处 Agent 的 reservedGroupIds 参数，恢复共享槽策略；有限
排队等待和缺测语义仍可保留以报告容量不足。完整源码回退则恢复本轮
scheduler 差异，不需要版本、数据格式或 Worker/Broker 边界迁移。

