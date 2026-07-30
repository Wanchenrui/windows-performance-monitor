# ADR-010：GPU 与温度采集使用隔离 Provider Worker

- 状态：Accepted
- 版本：0.7.1
- 日期：2026-07-30

## 结论

GPU、温度与厂商/驱动相关传感器不得加载到
`PerfMonitor.Agent` 进程。它们由当前用户权限下的
`PerfMonitor.ProviderWorker` 子进程采集；Agent 只依赖内部协议和
Worker 客户端，不引用 `LibreHardwareMonitorLib`。

Worker 不是通用插件宿主。v0.7.1 只允许编译时固定的硬件采集实现和
固定的 `collect` 操作。

## 原因

硬件库和驱动调用具有不同于 Win32 核心指标的故障模型：

- 厂商驱动或底层访问可能阻塞、崩溃或返回物理上无效的读数；
- 不同硬件与权限组合的覆盖率无法在开发机上穷举；
- 部分传感器需要管理员权限，但只读监控不应因此提升整个 Agent；
- 把库直接载入 Agent 后，进程内超时不能回收失控的原生线程或状态。

进程边界使 Agent 能在超时后终止整个 Worker 进程树，并把影响限制在
`gpu` 和 `sensors` 两个 capability。

## 信任与权限边界

1. Worker manifest 固定为 `asInvoker`，继承 Agent 的当前用户令牌。
2. Agent 不触发 UAC，也不以服务或管理员账户重启 Worker。
3. Worker 路径固定为 Agent 发布目录下的
   `provider-worker/perf-monitor-provider-worker.exe`。
4. `UseShellExecute=false`；不接受命令行、脚本、PowerShell、DLL 路径、
   Provider 名称或任意插件参数。
5. Worker 标准输出仅承载协议帧；标准错误写入 4 KiB 有界字节环，
   不能阻塞管道。
6. Worker 不写注册表、SQLite 或配置文件，不接触 Broker。
7. 硬件原始标识不进入外部契约或持久化层。设备 ID 是 Worker 生命周期内
   的类型加序号，不保证跨启动稳定。

权限不足时返回 `permission_denied` 或 `partial`。不得把未知值填成零，
也不得自动提权。

## 协议与资源边界

协议是 4 字节小端长度前缀加 UTF-8 JSON：

- 协议版本：`1.0`；
- 最大帧：1 MiB；
- 唯一操作：`collect`；
- 每个请求有随机 `requestId`，响应必须原样回显；
- 响应含 `workerInstanceId` 和单调递增 `sequence`；
- 名称、设备数、每设备传感器数均有确定上限；
- coverage 计数上限为 4,096，且必须满足
  `readable + skipped = enumerated`；
- 非有限数值、越界利用率和明显非物理温度在信任边界被拒绝。

Worker 返回的时间戳只作协议诊断字段；Agent 使用自己的调度上下文标记
freshness，不信任子进程时间。

Agent 对同一 Worker 的请求串行化。`gpu` 与 `sensors` Provider 在一个短
时间窗内共享同一次采集，避免重复访问硬件。

Worker 由 Job Object（可用时）设置关闭即终止及进程内存上限；无 Job
Object 能力时仍使用私有内存检查和 `Kill(entireProcessTree: true)`。
启动次数使用滑动窗口限制，防止崩溃循环消耗系统资源。

## 时间与故障语义

- 初始周期：5 秒；
- Provider 超时：10 秒，覆盖首次硬件库初始化；
- 同一 Worker 同时最多一个请求；
- 超时或协议错误立即废弃该 Worker 会话；
- 崩溃后的下一次采集可以重启，但必须服从重启预算；
- 超出重启预算映射为 `resource_exhausted`；
- Worker 缺失或当前机器没有对应硬件映射为 `not_supported`；
- 单个设备失败而其他设备可读映射为 `partial`。

Worker 失败不改变 CPU、内存、网络、磁盘、电源、SQLite、IPC 或 Desktop
的生命周期。

新增两个 Worker Provider 后，Agent 默认有界并发从 3 调整为 5。两个
硬件组最多占用新增的两个槽，原有核心 Provider 仍保留等价的 3 个槽；
Worker 内部仍严格串行，因而不会增加并发硬件访问。

## 数据语义

`gpu` 提供：

- GPU 设备数；
- 所有可读 GPU load 传感器的最大值；
- 所有可读 GPU 温度传感器的最大值；
- 按设备列出的最大 load 与最大温度。

这里的 load 是驱动公开的 GPU load 传感器集合最大值，不宣称等于某个
厂商定义的“核心利用率”。不同设备的利用率与温度不相加。

`sensors` 提供：

- 可读温度传感器数；
- 所有可读温度传感器的最大值；
- 按设备和传感器列出的读数。

聚合最大值用于历史与告警；设备明细仅进入实时快照。温度单位固定为
摄氏度。

## 依赖与许可

v0.7.1 固定使用稳定版 `LibreHardwareMonitorLib 0.9.6`，只存在于
Worker 项目依赖图中。NuGet 锁文件和第三方许可清单进入构建证据。

## 回退

v0.7.1 不修改 SQLite schema。回退到 v0.7.0 时可保留数据目录；旧版本
会忽略新增 metric ID。也可只移除 `provider-worker` 目录，此时 Agent
继续运行并把两个扩展组报告为 `not_supported`。
