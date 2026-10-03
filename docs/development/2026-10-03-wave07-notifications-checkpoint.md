# Wave07：诊断通知与免打扰

2026-10-03。版本保持 `1.1.0` 开发候选，没有创建发布标签。承接 [Wave06](2026-10-03-wave06-handoff.md)，继续小步实现用户功能。

## 已实现

- 诊断通知默认关闭，仅本次 Desktop 运行有效；诊断抽屉与托盘菜单均可开启 / 关闭。托盘不可用时禁用通知入口，诊断查询仍可使用。
- 免打扰可选 15 分钟、1 小时或手动结束，按单调时间计时。免打扰期间不请求系统气泡，恢复后不补发旧提醒。
- 开启后每 30 秒读取既有诊断，范围为近 5 分钟、最多 200 条，总查询超时 5 秒。仅当前 Agent 会话、有观测序号的活动 warning / critical 可进入通知；同次观察合并为一条请求，至少间隔 2 分钟。
- 按规则和对象的告警阶段去重：持续同一问题不重复提醒，已恢复后再次出现可重新提醒；同批最终状态已恢复则不提醒。冷却期间事件已消费，不在冷却结束补发。
- 开启、重连、会话变化、查询失败 / 截断、免打扰结束和超过 35 秒的轮询缺口会重新建立基线。去重集合有 4096 条上限；超限显示通知状态错误并保守停发。
- 隐藏时通知协调器继续读取原始会话；断连、停用、免打扰或退出会取消排队查询 / 显示。Dispatcher 真正请求气泡前再次核对取消与状态。通知不主动激活窗口，用户点击气泡才打开诊断。
- Windows 可能抑制通知或按系统设置播放声音；气泡接口返回成功只代表接受显示请求，不代表实际送达。没有宣称系统通知必定静音。设置不写入 Agent 配置，也不额外采集或执行优化动作。

## 本轮实际检查

- 新增相关回归 **20/20 通过，0 失败、0 跳过**：协调器 17 项、Desktop 集成 3 项。使用 fake tray 与不显示的 WPF 窗口，覆盖告警恢复 / 再激活、连续轮询和实际间隙、冷却、免打扰、UTC 变化、查询超时、取消晚结果、显示异常、稳定控件、缺托盘和不激活窗口。[日志](../../artifacts/feature-wave07-notifications/notification-tests.log)、[TRX](../../artifacts/feature-wave07-notifications/test-results/notifications.trx)。
- Desktop Release 构建 **0 警告、0 错误**：[日志](../../artifacts/feature-wave07-notifications/desktop-release-build.log)。使用隔离 SDK `10.0.302` 与 `ChecksumAlgorithm=SHA1`，没有运行 SHA-256、正常 Agent / Worker、Broker 或旧验收脚本。
- 主代理使用真实 Desktop 窗口的开发 host，接独立命名管道 `PerfMonitor.Wave07.NotificationPreview` 的模拟数据源。窗口 `ShowActivated=false`，没有真实托盘 / 全局热键、键鼠输入或窗口激活。host 仅设置自身窗口状态，以展示通知设置与 15 分钟免打扰。
- [副屏截图](../../artifacts/feature-wave07-notifications/01-secondary-notifications.png)显示通知设置、免打扰状态、四个诊断范围按钮与主趋势并排，文字 / 控件无重叠。后台 PrintWindow 校验本轮 PID、HWND、路径与标题，实际边界 `(-1525,-5,-275,945)`，完全位于左副屏。[位置日志](../../artifacts/feature-wave07-notifications/secondary-preview-stderr.log)、[捕获日志](../../artifacts/feature-wave07-notifications/secondary-capture.log)。这是单次布局观察，不代表完整混合 DPI 或真实托盘送达验收。
- 开发 host 120 秒自动关闭，模拟源 150 秒自动结束；运行元数据与源码仅在被忽略的 `artifacts/` 中，不加入提交。此前 Wave06 预览和所有历史记录未覆盖。

未重跑旧全量、基线采样、CheckOnly 或发布验收，也未核对本次远端 CI。实际系统气泡送达、托盘菜单鼠标操作、正常 Agent / Worker 完整联调、实际温度、默认快捷键和完整多屏混合 DPI 仍未验证。

## 后续

用户已要求继续智能调度，并明确交互流畅、任务吞吐、节能应全部作为可选偏好。通知相关检查通过后已开始下一步：根据现有新鲜观测自适应调整本软件 GPU / 温度查询周期，带 Agent 侧时限和恢复；基础采集保持原设置。此处不将尚在开发的调度功能或电脑性能收益描述为已经验证。
