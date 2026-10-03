# Wave08：三偏好智能采集调度

2026-10-03，承接 [Wave07 通知与免打扰](2026-10-03-wave07-notifications-checkpoint.md)。版本保持 `1.1.0` 开发候选，没有创建发布标签。

## 范围与交互

用户要求先完成可关闭的诊断通知与免打扰，再开始智能调度与电脑性能优化，并明确交互流畅、任务吞吐、节能三种偏好都必须让用户可选。

本轮首个执行范围是本软件自身的可选硬件采集：默认关闭，根据既有可靠观测调整 GPU / 温度请求周期。CPU、内存、网络、磁盘与进程采集保持原设置。三种偏好均是用户选择，不自动替用户决定偏好。此阶段减少的是监控的可选请求频率，不能表述为已验证电脑响应、任务完成时间、续航、温度或帧率收益。

界面使用独立按需抽屉，保留主趋势；提供偏好、15 / 30 / 60 分钟期限、启用、关闭并恢复和切换偏好。展示实际确认的周期、剩余期限和依据，具体建议按需展开。切换偏好不延长当前期限。连接变化或操作未确认时重新读取实际状态，旧 Agent 能力缺失时不发送设置请求。

Agent 持有单调时间期限，Desktop 关闭不影响到期恢复。首次启用保存原倍率，关闭 / 到期恢复原值，外部变更发生冲突时不覆盖。手动轻量模式的暂停仍独立且优先。没有新增对其它进程的优先级 / 电源动作或系统设置修改。

## 当前策略

以下门限是首版启发式规则，不是效果已验证的最优参数。倍率相对注册基准；若启用前已经更慢，保留原来更慢的设置。

| 用户偏好 | 减频依据 | 倍率 | 恢复依据 |
| --- | --- | --- | --- |
| 交互流畅 | 新鲜 CPU ≥70%，新观测连续 10 秒 | 3 | CPU ≤55% 连续 20 秒 |
| 任务吞吐 | 新鲜 CPU ≥85%，新观测连续 15 秒 | 6 | CPU ≤70% 连续 30 秒 |
| 节能 | 可靠电池供电或系统省电已开启 | 12 | 可靠交流供电且省电关闭，新观测连续 20 秒 |
| 节能的 CPU 分支 | 未采用电源减频时，CPU ≥80% 连续 15 秒 | 6 | CPU ≤65% 连续 30 秒 |

电源减频的依据失效（未知、缺测、陈旧）时立即退出该原因的减频并恢复原值，随后只能凭重新积累的 CPU 证据减频。无电池本身不是故障。CPU 证据缺失或连续性不足时保守恢复原值。仅基础周期可确认，失败退避 / 正在运行的调用仍可能令实际新观测更晚。

## 数据与实现约束

- 仅 GPU / 温度采用 1 / 3 / 6 / 12 倍周期，基准取实际 Provider descriptor；当前默认基准 5 秒时对应 5 / 15 / 30 / 60 秒。失败退避仍独立生效，配置周期不等于保证的实际成功读取间隔。
- 持续负载判断仅依据当前 Agent 会话内的新鲜、有限值和新观测序号。重复快照不能积累持续证据，缺测不补零；UTC 调整不能影响租约或持续时间。
- 新观测携带内部期望周期，新鲜度预算只能随更快周期收紧，不能因再次减频让旧陈旧数据恢复新鲜。原外部 SnapshotGroup JSON 结构保持兼容。
- Sampler 报告该次执行的期望周期；界面条形表示配置请求频率相对基准频率，不作为节省 CPU、功耗或 Worker 调用次数的实测百分比。
- GPU / 温度共享 Worker 的短时复用机制，不能由倍率直接推算 Worker 调用减少比例。GPU Engine 备用负载在更长请求周期下是更长差分窗口的读数，不能冒充温度或完整显卡信息。

## 本轮实际检查

- 本轮 **76 个不同的相关 case 全部通过**，共执行 78 次、0 失败、0 跳过；新增调度 48 个 case，既有受影响回归 28 个。不是全量回归，也不与 Wave07 的 20 项相加冒充最新全量。
- 第一批 Desktop **17/17**：新 UI / 操作协调 14 项，受构造 / 退出接线影响的通知集成 3 项。[日志](../../artifacts/feature-wave08-adaptive/desktop-tests-01.log)、[TRX](../../artifacts/feature-wave08-adaptive/desktop-test-results/adaptive-desktop.trx)。窗口未显示，未发送真实通知。
- 第二批 IPC / 展示修正 **18/18**：新命名管道回归 17 项，加复查运行中期限选择不假高亮的单一 Drawer case。涵盖旧能力不发送新命令、非法字段 / 实例 / 时限返回 `invalid_request` 且连接继续可用、真实连接 / hello / mutation 等待取消。[日志](../../artifacts/feature-wave08-adaptive/ipc-tests-01.log)、[TRX](../../artifacts/feature-wave08-adaptive/ipc-test-results/adaptive-ipc.trx)。
- 最后一批后端相关 **43/43**：新策略 / 时钟 / 数据质量 15 项、新假时钟调度 2 项、直接受影响的旧 Scheduler / LightMode / SnapshotAssembler / 快照共享 25 项，以及 IPC 缺 required 响应解析修正后的单一 case。包含三偏好、没有 Desktop 读取时到期恢复、冲突保护、手动暂停、未知电源恢复、重复 / 非有限 / 旧会话 / UTC 回拨、慢快慢不复活旧数据、一个基础周期内恢复和基础组频率不变。[日志](../../artifacts/feature-wave08-adaptive/backend-tests-01.log)、[TRX](../../artifacts/feature-wave08-adaptive/backend-test-results/adaptive-backend.trx)。
- 明确排除了旧 `SchedulerIsolationTests.CapacityTimeoutHasNoObservationAndCannotResolveAnActiveCpuDiagnosis`，因为其诊断事件生成路径使用用户禁止的 SHA-256。新测试只用假 Provider、假时钟、控制器、序列化与隔离命名管道，没有运行 DiagnosticEvaluator / DiagnosticEngine / SQLite 存储或正常 Agent。
- 最终 Agent 与 Desktop Release 构建均 **0 警告、0 错误**：[Agent 日志](../../artifacts/feature-wave08-adaptive/agent-release-build.log)、[Desktop 日志](../../artifacts/feature-wave08-adaptive/desktop-release-build.log)。仍使用隔离 SDK `10.0.302` 和源校验选项 `ChecksumAlgorithm=SHA1`；不修改全局运行时。
- 主代理已验收[副屏调度截图](../../artifacts/feature-wave08-adaptive/01-secondary-adaptive-energy.png)：真实 WPF 窗口接独立 `PerfMonitor.Wave08.AdaptivePreview` 模拟 IPC，显示三偏好、已启用节能、15 分钟剩余、5 秒→60 秒两组周期和主趋势并排，无重叠。[位置日志](../../artifacts/feature-wave08-adaptive/secondary-preview-stderr.log)、[后台捕获](../../artifacts/feature-wave08-adaptive/secondary-capture.log)。实际边界 `(-1525,-5,-275,945)`，完全在左副屏；`ShowActivated=false`，无键鼠输入、无托盘 / 热键、无激活。此模拟状态验证布局与 IPC 展示，不作为真实负载调优效果或正常 Agent 联调证据。
- 预览 host（6032）与模拟源（32544）按 120 / 150 秒期限退出，已按 PID / 路径核对均不再运行；本轮临时产物全部留在忽略目录，旧日志、数据库、截图与中间记录未覆盖。
- 静态交叉审阅未留明确 P1/P2。末次只补了通知错误说明：容量满时提示重启界面后重开，显示请求失败时提示查看诊断；不再将这两类错误误写为查询等待重试。这是展示文字修正，未重复跑通知协调器整套测试，已包含在最终 Desktop 构建。

## 仍保留的边界与下一步

正常 Agent 的既有指纹路径会执行用户禁止的 SHA-256，本轮没有启动该路径，也没有修改产品指纹逻辑来绕过约束。真实温度、正常 Agent / Worker 完整联调、实际系统气泡送达、完整鼠标 / 托盘 / 热键 / 多屏混合 DPI 和实际电脑性能收益继续标为未验证。没有核对此次远端 CI，没有重跑旧全量、72 小时、OS 矩阵、签名、性能预算或 CheckOnly。

默认输出周期为 1 秒；若用户自定义 `--output-period-ms` 超过 3000，CPU 策略会因连续性不足保持 / 恢复原周期。这是当前保守边界，未额外增加采集循环。假时钟调度测试中有一处短暂等待续体的真实 20 毫秒等待，本次通过；若后续出现偶发失败，应改明确的定时器注册屏障，不靠增加等待时间。

下一项建议沿三偏好继续电脑性能优化：先让用户选择具体进程实例和允许的动作，显示预期变化、原值与期限，再实现可撤销的单进程调优。现有 Broker 动作还缺修改前原值的耐久记录与冲突恢复，不将当前的建议或采集减频冒充已经修改其它进程。软件自身手动轻量模式的定时恢复也仍可作为小步候选；本轮已有时限的是智能采集调度。

## 提交与推送

本轮调度源码、测试及检查点已提交为 `e66f39c5d29b566075d1ee361e0b3f6d7071bf10`（`feat: add bounded adaptive collection scheduling with three preferences`），共 22 个文件。`git push origin HEAD:refs/heads/codex/v1.0.0-release-architecture` 返回 exit 0，远端确认 `d287b74..e66f39c`，没有 force push。源码提交已包含 Wave07 的实际提交 / 推送及清理结果，当前续记补充本次调度提交的实际结果；下一项功能见上一段。临时预览、截图、运行数据、日志及 bin/obj 未加入提交。
