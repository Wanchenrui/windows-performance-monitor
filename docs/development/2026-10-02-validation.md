# 2026-10-02 开发验收记录

**状态：本轮程序级验证完成；限定范围的新版 UI 视觉验收通过。** 主 Agent 已审阅最终 Release 的真实窗口与 integration-host 截图。该结论只覆盖本文列出的窗口状态和当前 125% 显示缩放，不包含物理托盘/热键操作、多 DPI/多屏或发布验收。

## 基线与环境

- 验证基线提交：`74d29ddfc8a29fac23af5d57068b2aef08c42804`。
- 主机：Windows 10 专业版 22H2，build 19045，x64；Intel Core i7-14700KF，28 个逻辑处理器，物理内存 34,104,135,680 字节。
- 本机 Windows 10 仅是本轮开发运行环境，不在仓库承诺的 Windows 11 24H2、Windows 11 25H2、Windows Server 2022 Desktop Experience、Windows Server 2025 Desktop Experience 四目标支持矩阵内；本轮没有验证或扩大该支持声明。
- .NET：按仓库 `global.json` 使用 .NET SDK `10.0.302`，对应运行时 `10.0.10`。SDK 安装在用户本地隔离目录 `C:\Users\Administrator\AppData\Local\PerfMonitor\dotnet-sdk-10.0.302`，未更改系统默认 SDK 或 PATH。下载包与 Microsoft 官方 release metadata 中的 SHA-512 相符；安装和 metadata 记录见 `artifacts/validation/dotnet-sdk-install.log`、`artifacts/validation/dotnet-10.0-release-metadata.json`。
- Python：本机系统 Python 保持 `3.14.4`。最终测试使用用户本地隔离的 Astral uv 管理运行时 CPython `3.12.10` 和独立 venv `C:\Users\Administrator\AppData\Local\PerfMonitor\venv-3.12.10`；未注册解释器或更改系统 PATH。项目与 CI 均要求 Windows x64 / CPython `3.12`，CI 未固定补丁版本，因此 `3.12.10` 是本轮本地测试版本，不改变项目或 CI 的版本声明。uv 发布来源、校验和运行时指纹见 `artifacts/validation/uv-release-metadata.json`、`artifacts/validation/python-install.log`。
- 工作树：Git HEAD 仍为上述基线，产品实现和验收记录保留在未提交工作区。`artifacts/validation/final-artifact-hashes.json` 记录五个最终 Release apphost 哈希、五个输出目录指纹和最终验证日志/TRX 哈希，用于辨认这次实际测试的产物。
- 分工：主 Agent 负责架构边界、宏观协调和界面验收；High 负责实现；Max 处理较难的实现问题；Luna 负责验证与证据整理。

## 本阶段实现范围

- **WP02（存储与隐私投影）：** 当前 schema 为 3，新写入的快照在持久化前经过最小化投影。无进程观察名单时不保存进程明细，卷信息只保留系统卷利用率等白名单数据。旧 schema 迁移和备份保留原有历史数据；这轮没有对历史快照、备份或其他旧敏感数据做追溯清除。存储表仍叫 `snapshots_raw`，不能仅凭表名推断新写入包含完整原始载荷。
- **WP03（时间与诊断回放）：** 快照/分组增加单调时间、观测序号和投递顺序元数据（`AgentSnapshot.ElapsedSeconds`、`AgentSnapshot.DeliverySequence`、`SnapshotGroup.ObservedElapsedSeconds`、`SnapshotGroup.ObservationSequence`）；诊断事件增加 `DiagnosticEventContract.ObservationSequence`。UTC 时间用于展示和查询，诊断回放按逻辑时间及顺序工作。旧历史 DTO 仍可读取，但缺少单调元数据的旧快照拒绝回放并返回 `diagnostic_replay_time_unavailable`；诊断策略或精简投影不兼容时拒绝回放并返回 `diagnostic_replay_projection_incompatible`。诊断事件 ID 改由观测序号相关键生成；既有记录不回填、不重算。
- **WP05（首段界面更新）：** 桌面端使用最新状态合并/节流更新，窗口隐藏时跳过待渲染更新并在恢复时呈现最新状态，关闭窗口后丢弃排队更新。质量状态明确区分有效零值、缺失/非有限/预热中、过期/断连、部分覆盖和分组缺失。本阶段定向逻辑测试通过；最终候选还完成了本文限定范围内的截图视觉验收。

### WP03 交叉复核与兼容性

- `AgentSnapshot.DeliverySequence` 区分 Snapshot/provider `Sequence` 与 fanout 实际交付序号。`SnapshotFanout` 每次实际投递分配序号；直接调用 consumer 时也为每次投递尝试编号，使真实队列丢帧可显式形成 gap。这样不会把多个 Provider 更新造成的自然 Snapshot 序号跳跃误判成丢帧；实际 Fanout 缺口或有界 Channel 丢帧会中断诊断连续性。该 P1 修正经真实三 Provider / 33 次交付与默认 debounce 用例、真实有界 Channel 丢帧用例复核；`WarmSamplerIsUnknownAndCannotSupplyASamplingGap` 覆盖预热采样不构造采样缺口。持久化保留按 `DeliverySequence` 顺序实际投递的精简快照，标量观测仍按 `ObservationSequence` 去重。
- `DiagnosticEventContract.ObservationSequence` 标识事件最后关联的逻辑观测序号。诊断查询按 Agent 实例 epoch 与观测序号合并近期/持久化记录，同一实例中的最新 `active` / `resolved` 不按 UTC 排序；UTC 只用于查询区间筛选。Desktop 只基于当前实例与观测序号判断当前状态。旧诊断事件没有 `ObservationSequence` 时显示“状态未知”，结果被截断时显示不完整；不会把旧历史误判为当前正常状态。`EventObservationOrderSurvivesUtcRollbackInRealtimeQuery` 覆盖 UTC 回拨下实时事件顺序，相关检查覆盖数据库、Agent 查询与 Desktop 状态摘要。
- 交叉复核关闭了 WP03 Provider 自然跳号被误判丢帧 P1、WP02 跨版本迁移/WAL 与 Replay 读取上限 P2，以及持有投递顺序问题。真实三 Provider cadence、实际 Channel 丢帧、UTC 回拨事件排序、预热 Sampler 等回归通过；合并复核证据为 `artifacts/validation/dotnet-temporal-storage-review.log`、`artifacts/validation/temporal-storage-review.trx`（62/62），WP03 专项为 `artifacts/validation/dotnet-temporal-quality-tests.log`、`artifacts/validation/temporal-quality.trx`（37/37）。这些筛选集与全量测试重叠，不相加。
- 已知兼容性影响：新增 nullable 元数据对旧 DTO/消费者是附加字段，但旧快照没有足够单调元数据时不能参与新诊断回放；旧诊断事件缺少 `ObservationSequence` 时客户端不能可靠恢复当前状态。旧快照与诊断记录仍保留，不追溯清除或回填。存储队列的字节预算限制 UTF-8 序列化 payload 字节数，不代表进程总 heap 或 Working Set 限额。

## 最终统一验证

| 验证 | 结果 | 证据 |
|---|---:|---|
| NuGet locked restore | 21 个项目检查成功，退出码 0 | `artifacts/validation/final-dotnet-locked-restore-ui-wave.log` |
| .NET Release 全量测试（最终 UI 候选） | 204/204 通过，0 失败、0 跳过；Actions 33、AgentTests 123、Broker 27、Contract 11、ReleaseTests 10 | `artifacts/validation/final-dotnet-tests-ui-wave.log`、`artifacts/validation/final-dotnet-trx-ui-wave/` |
| .NET Release 全解决方案 build（最终 UI 候选） | 21 个项目通过，0 警告、0 错误 | `artifacts/validation/final-dotnet-build-ui-wave.log` |
| CPython 3.12.10 锁定环境与全量测试 | `requirements-dev.txt` 按哈希安装成功，`pip check` 无冲突；76/76 通过。UI 收敛未改 Python 代码，因此复用此前结果 | `artifacts/validation/python-dependencies-setup.log`、`artifacts/validation/final-python-tests.log` |
| 最终 Agent WAL 异常终止/重启 | schema 3；WAL 确认；终止后与重启后 integrity 均通过；重启前 5 条、之后 52 条，实例 ID 改变 | `artifacts/validation/final-crash-recovery-schema3.log`。结果绑定 Agent SHA-256 `1485AB64B79360AE9D6D9181A04E6F9E99F878A2BE746F1758B2C6BD8099F51A` 与 Support SHA-256 `577DA8FF5E0E097E4481FCFE7876806C2169B423DC01C7C7B9B740F274A28DEF`。日志保留一次缺少本地 `DOTNET_ROOT` 的启动失败及显式设置后的成功重试。 |
| package lock 审计 | 5 份变更 lock 文件；无新增 NuGet 包 ID，所有保留包版本及 content hash 不变 | `artifacts/validation/final-lockfile-review.log`。测试 lock 文件移除了 3 个因 net10 目标图不再可达的 `System.*` 传递包；项目引用拓扑变化见锁文件差异。 |
| 最终产物和证据 SHA-256 | Agent、Desktop、Support、Broker、ProviderWorker apphost 及其输出目录指纹、最终日志/TRX 已记录 | `artifacts/validation/final-artifact-hashes.json` |

Agent smoke 原始资源采样有 20 条：Working Set 最小值 `2,113,536` 字节、最大值 `105,828,352` 字节，累计 CPU 观测最大值 `2.75` 秒。样本包含启动阶段，本记录仅保留观测值，不据此推断稳态性能。该次启动采用隔离数据目录、dry-run-only 策略，未安装或启动 Windows 服务，未启动 Broker，也未执行特权动作。数据库快照检查显示写入投影不含进程明细；相关原始样本、资源 CSV、数据库和 `result.json` 位于上述证据目录。

本阶段只有这次约 18 秒的 Agent M0 短时证据，不是完整全产品基线，不代表用户桌面端、Broker、安装器、全目标系统或 72 小时运行的整体性能和稳定性。早期 Python 3.14 检查未用于最终结果；最终 Python 回归按项目哈希锁在 CPython 3.12.10 隔离环境中完成。

## 界面验收状态

主 Agent 已查看最终候选图并通过本文明确列出的视觉范围。用户曾用 Esc 中止 Computer Use，因此最终视觉检查改用获准的后台截图流程：Win32 `PrintWindow(PW_RENDERFULLCONTENT)` 从真实 WPF HWND 捕获，没有 UI Automation、鼠标/键盘注入或合成快照。显示缩放为 125%（窗口 DPI 120）。

真实 Desktop exe 证据在 `artifacts/validation/ui-screenshots/20261002-201320-final-hud-main/`。它展示主程序紧凑连接态、等待态及断连后保留历史状态；Desktop exe SHA-256 为 `4BCF204CBB28B815E65AA4459EE35098BECA92261EF4658D968C2BE978BB2B04`，Desktop DLL 为 `6A7285EA01480E475F59F5097B78E996A8B4C0E97A35F31C0C44CFA972BB1BA4`，Agent exe 为 `1485AB64B79360AE9D6D9181A04E6F9E99F878A2BE746F1758B2C6BD8099F51A`。主窗口实际紧凑尺寸为 760×200 DIP / 950×250 像素；Desktop PID 44188 与隔离 Agent PID 47992 均已退出，Agent 实际运行 20.37 秒并以退出码 0 结束。

STA integration-host 证据在 `artifacts/validation/ui-screenshots/final-hud-layout/`。它加载同一最终 Release Desktop DLL，并通过 Dispatcher 调用产品的 `SelectResource(Network)`、`OpenPanel(Processes)` 等公开 UI 路径，呈现真实 Agent 数据；窗口底部明确标为“集成测试宿主”。它没有启动最终 exe 的 `DesktopProgram` 托盘/热键集成，因此其图只用于验收网络展开视图与进程抽屉布局。两张重点图分别为 `host-02-network-trend.png`（SHA-256 `88B363F46679594FF4714A3464FC9DE02A48A36835E418E639EA7E7F4B986163`）和 `host-03-process-drawer.png`（SHA-256 `153AD17DD020EA2EFC734971D9835D8A969B6833C48190420F9E76545FE44AC7`）。展开窗口为 1000×680 DIP / 1250×850 像素。Host PID 39432 及隔离 Agent PID 43696 正常退出，Agent 实际运行 30.42 秒、退出码 0；采集后没有本轮 Agent、Desktop 或 LayoutHost 残留。完整截图哈希、窗口矩形/DPI、进程和运行参数见两目录中的 `capture-evidence.json`（host 目录文件名为 `layout-host-evidence.json` 与 `launch-evidence.json`）。

可在前台 PowerShell 运行开发预览入口 `artifacts/preview/Start-Preview.ps1`：`powershell -NoProfile -File artifacts/preview/Start-Preview.ps1`。它为本次 Agent/Desktop 子进程设置隔离的 .NET runtime 环境，不修改系统 PATH；使用单独数据目录和日志，启动前检查现存 PerfMonitor 进程与当前用户 pipe。该脚本是本地开发预览，不是安装器；不启动 Broker、服务或动作。预览窗口显示期间控制台保持前台，用户应从 Desktop 托盘菜单显式退出，脚本随后清理本轮 Agent；Ctrl+C 会运行 `finally` 清理，强制结束 PowerShell 不保证子进程清理。最终版本的 `-CheckOnly` 已退出 0，确认 exe/DLL hash 与最终产物相符，输出保存在 `artifacts/validation/preview-checkonly-ui-wave.log`；本轮没有通过该入口启动第二个完整预览实例。

视觉通过范围仅包括当前 125% DPI 下的真实主程序紧凑/等待/断连历史状态，以及真实窗口 host 的网络展开/进程抽屉布局。没有进行物理托盘菜单、热键触发、焦点竞争、其他缩放比例、多显示器或完整矩阵的人工操作验收；不得把截图验收扩展成这些行为已通过。

## 尚未完成项

- WP01 深层快照优化、WP04、完整产品基线与 72 小时实测、场景级验收及动作恢复路径验收均未完成；短时 Agent smoke 未触发动作。
- 本地托盘/热键、焦点竞争、100%/150%/200% 与混合 DPI 多屏行为未实测；当前 UI 结论只涵盖截图所列场景。
- 本机 Windows 10 不属于四目标支持矩阵，未执行 Windows 11/Server 部署矩阵验证；本轮不扩大系统支持声明。

以上结果不构成产品发布、长期性能、72 小时稳定性、Windows 11/Server 支持矩阵或实机动作安全结论。
