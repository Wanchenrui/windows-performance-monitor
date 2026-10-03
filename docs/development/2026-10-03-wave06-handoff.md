# Wave06 接手与范围导出

2026-10-03。版本保持 `1.1.0` 开发候选，没有创建发布标签。

## 已整理并推送 Wave03–05

- 接手分支：`codex/v1.0.0-release-architecture`；接手 HEAD：`73ec33fd5c27e2658d5ce2c514388b71eb9a406b`。
- 既有源码、相关测试、开发工具、检查点和证据元数据已整理为提交 `446b39a4688384ce1393ea64c919d4e58d0977d0`（`feat: preserve wave03-05 monitoring and desktop improvements`），共 58 个文件。
- `git push origin HEAD:refs/heads/codex/v1.0.0-release-architecture` 返回 exit 0，远端确认 `73ec33f..446b39a`。没有 force push。
- 本机缺少 Git 作者配置，首次提交未创建提交；重试仅通过该命令的 `-c` 参数沿用最近两次提交的 `Wanchenrui` / GitHub noreply 作者信息，没有修改全局配置。
- 此次整理没有重跑测试、构建、采样、CheckOnly 或界面验收。引用 [Wave05](2026-10-03-feature-wave05-checkpoint.md) 已有 100 项相关回归（70 后端、30 Desktop）和 Release 零警告零错误记录；不将它们描述为本次新执行或最新全量结果，也未核对此次推送的远端 CI。
- 提交只包含源码、测试、工具与开发文档 / 证据索引。`artifacts/`、日志、截图、数据库、运行数据、`bin/obj` 保留本地；证据链接指向本地原件，不声称远端可下载原件。旧检查点中的“未提交”是对应时点的历史状态，由本续记补充，不覆写旧证据。

## 本轮新增：按范围导出

首版源码与 10 项回归已提交为 `1da87f532c9940619dd2176ac748ce917d92e0fd`（`feat(desktop): export bounded trend and diagnostic ranges`）；推送 exit 0，远端确认 `446b39a..1da87f5`。之后副屏视觉检查发现范围下拉框对比度不足，追加小幅界面修正，见下面实际验收记录。

已实现独立版本 1 的本地 JSON 导出格式，保持现有 Agent IPC 契约：

- **趋势：**在图形下方选择 60 秒 / 5 分钟，再点“导出趋势 JSON”。同步冻结当前资源或选中进程的 Desktop 缓冲，使用缓冲末端的单调时间闭区间；网络 / 磁盘包含双线，保留单位、UTC、单调时间、观测序号、null 缺测、质量文本、断点与会话 / 进程身份。每线导出首点断开，不修改原曲线。只导出已有观测；隐藏 / 断连 / 丢帧期间不补零，缓冲末端不冒充保存时刻，进程仍只含选中后的新观测。质量是当时界面的摘要文本，并非完整原始质量契约。
- **诊断：**抽屉可选择最近 60 秒 / 5 分钟 / 1 小时 / 24 小时。导出会重新查询范围，最多 2000 条，保留查询条件、事件、原始证据、截断状态及各事件所属会话。筛选口径为事件 `LastSeenUtc` 的 Unix 毫秒闭区间，不裁剪跨边界的证据，不将历史事件当作当前状态或完整生命周期。旧协议未报告本次持久存储查询是否成功，导出 `storageAvailable=null`，界面说明“存储覆盖未知，空结果不代表没有诊断”。截断时提示缩小范围。
- **交互与文件：**原生保存对话框处理路径和覆盖确认；导出控件跨刷新保持身份，操作状态单独保留。连接变化会取消查询 / 保存并核对响应的会话与查询条件；关闭时取消并等待导出任务。文件先在目标同目录完整写入临时文件，完成后替换；发布前失败 / 取消不先删除原文件，发布后状态变化明确提示“文件已保存”。
- **边界：**不额外采集、不直接读取数据库、不更改产品指纹，不把统计历史接口缺少的质量 / 缺测信息补写成事实。

## 本轮实际检查

- 新增 `DesktopRangeExportTests` **10/10 通过，0 失败、0 跳过**：[测试日志](../../artifacts/feature-wave06-export/range-export-tests.log)、[TRX](../../artifacts/feature-wave06-export/test-results/range-export.trx)。覆盖单调闭区间与 UTC 回拨、双线与单位、零值 / null / 陈旧、冻结与会话变化、缺组、PID 复用、诊断集合与证据复制、查询边界 / 计数 / 非有限值、文件替换以及取消 / 序列化 / 重命名失败。
- Desktop **Release 构建 0 警告、0 错误**：[日志](../../artifacts/feature-wave06-export/desktop-release-build.log)。只针对本轮新增导出运行相关回归与 Desktop 构建，没有重跑旧全量、基线或 CheckOnly。
- 使用既有隔离 SDK `10.0.302`，构建显式使用源校验选项 `ChecksumAlgorithm=SHA1`；没有执行 SHA-256、策略指纹或旧预览 / 验收脚本。
- 新模拟 IPC fixture 仅在忽略目录 `artifacts/feature-wave06-export-preview-fixture`，从既有预览复制后扩充范围内外事件、跨范围证据、旧会话以及趋势缺测 / 部分可用 / 陈旧 / 序号缺口。首个 `--no-restore` 构建因新目录缺少 assets 文件失败，保留 [原日志](../../artifacts/feature-wave06-export/fixture-build.log)；补锁定还原后构建成功，见 [续日志](../../artifacts/feature-wave06-export/fixture-build-restored.log)。旧 fixture 和证据未覆盖。

## 窗口验收与双屏续记

- 正常 Desktop 可执行文件接模拟 IPC，已通过键盘展开趋势、打开原生 JSON 保存对话框，随后窗口显示“已取消导出”；诊断抽屉实际查询到模拟事件。没有完成通过原生对话框成功保存 JSON 的全流程操作，文件格式与写入失败边界由上述 10 项回归覆盖。
- Computer Use 元素点击受 `coordinate input geometry is unavailable` 限制，保存对话框的元素设置也未成功；WGC 截图 `FrameArrived timed out`。使用此前授权的、校验本轮 PID / 可执行文件路径 / 窗口标题的后台 PrintWindow 捕获，原截图保留为 [01](../../artifacts/feature-wave06-export/01-export-drawer.png)。人工同时操作导致输入竞争，未将竞争中的焦点操作当作验收通过。
- 用户要求主屏留给本人，后续停止自动键鼠输入。原主屏 Desktop 预览已清理；新增仅开发用 WPF host `artifacts/feature-wave06-export-secondary-preview`，直接载入当前 Desktop 窗口，设置 `ShowActivated=false`，无托盘 / 全局热键，仅在自己的窗口调用网络趋势 / 诊断入口。该 host 不属于产品提交。
- DPI-aware host 识别左侧副屏物理边界 `(-1800,-1105,1800,3200)`，窗口实际边界为 `(-1525,-5,-275,945)`，完全在副屏；先前 PowerShell 读到的 1440×2560 属于缩放坐标。见 [位置日志](../../artifacts/feature-wave06-export/secondary-preview-stderr-v3.log)、[后台截图与位置](../../artifacts/feature-wave06-export/secondary-capture.log)和 [02 截图](../../artifacts/feature-wave06-export/02-secondary-export-layout.png)。这是单次副屏放置，不能代表完整混合 DPI 验收。
- 02 截图发现诊断范围原生下拉框在深色主题下对比度不足，因此改成四个稳定复用的深色范围按钮，并保留选中 / 忙碌 / 可访问性状态。追加 Desktop Release 构建 **0 警告、0 错误**，见 [修正后日志](../../artifacts/feature-wave06-export/desktop-release-build-range-fix.log)。这是展示修正，没有重跑已通过的 10 项数据 / 文件回归。修正后的副屏 host 已运行且模拟诊断查询返回 5 条，但尚未取得修正后最终截图。
- 副屏 host 首次因硬编码缩放坐标没有找到目标显示器而安全退出，v2 的开发 host 构建有一项未等待返回值警告；两份原记录保留。修正屏幕选择和返回值后，v3 host 构建零警告零错误，见 [host 构建](../../artifacts/feature-wave06-export/secondary-preview-build-v3.log)。这些开发 host 结果与产品构建分开记录。
- 最后一次只读窗口枚举收到用户物理 Esc 停止 Computer Use 的通知。随后没有继续调用 Computer Use、截图或输入工具；仅整理仓库文档 / 提交。副屏预览保留供用户自行关闭，模拟数据源具有 600 秒自动停止期限；不宣称这两个进程已清理。实际进程记录分别为 [副屏 host](../../artifacts/feature-wave06-export/secondary-preview-process-v3.json)与 [模拟数据源](../../artifacts/feature-wave06-export/fixture-secondary-process.json)。

最终静态交叉审阅没有未解决的明确 P1/P2。没有核对此次新提交的远端 CI。正常 Agent / Worker 全链路、实际温度、原生保存对话框成功保存 / 覆盖确认完整操作、所有范围按钮实操、鼠标 / 托盘 / 默认快捷键 / 焦点竞争及混合 DPI 全覆盖仍未验证。

## 下一项功能

下一候选为可关闭的诊断通知 / 免打扰，再考虑软件自身轻量模式定时恢复。只做与新增功能直接相关的检查，继续保留未验证的硬件、完整 Agent / Worker、鼠标 / 托盘 / 默认快捷键 / 混合 DPI 等边界。

整个接手过程未计算或校验 SHA-256，未运行旧的含摘要计算的预览 / 验收脚本，未启动正常 Agent、服务或 Broker。
