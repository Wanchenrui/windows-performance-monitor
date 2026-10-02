# Desktop 驻留入口与交互验收计划

开发基线为 `348a6091c5eb10b55baa87850263df0eae306634`，版本 `1.1.0`。
用户选择本轮保持后台开发，暂不操作前台。本记录不表示真实托盘、默认
快捷键、焦点竞争或多屏输入已经验收；旧报告、旧截图与旧证据保持原样。

## 正常产品的隐藏启动

Desktop 新增 `--start-hidden`，通过已有 ResidentController 创建托盘、
消息 HWND 与快捷键，并启动既有 Agent 会话。启动路径不调用 Show 或
Activate；渲染维持暂停。之后用户从原托盘或快捷键入口恢复同一窗口时，
显示最新状态并继续原订阅。无参数入口保持原有可见启动和托盘失败回退。

隐藏启动缺少可用托盘时写入 `desktop_start_hidden_tray_unavailable`，
清理并返回退出码 `3`，不会弹出可见回退窗口。未知或重复参数写入
`desktop_invalid_arguments` 并返回 `2`。这是一项正常产品选项，不是
专用测试后门；本轮没有添加外部 ShowWindow、UI Automation 或输入注入。

## 避免启动旧产物

旧本地入口 `artifacts/preview/Start-Preview.ps1` 固定指向 `src/**/bin`。
本轮只读运行其 `-CheckOnly` 返回 `0`，但四个产物仍是原 `1.0.0`；
因此该入口不用于本轮新产物。旧 `artifacts/version-1.1.0` 也绑定
`74d29dd` 基线，不能替代当前提交产物。

新增维护入口 `scripts/start_desktop_preview.ps1` 必须显式提供独立
`-ArtifactsPath`，默认只做 CheckOnly。它从仓库根定位文件，校验 Agent
与 Desktop 的四个 EXE/DLL 的 FileVersion 和完整提交后缀，拒绝旧输出，
并记录两个完整依赖目录指纹、HEAD 和工作树状态。提交后缀不能证明未提交
源码的精确状态，独立构建日志与代码审查仍需一同保留。

最终统一构建使用 `--artifacts-path artifacts/development-348a609`。
构建后在仓库根执行纯预检：

```powershell
powershell -NoProfile -File scripts/start_desktop_preview.ps1 -ArtifactsPath artifacts/development-348a609 -CheckOnly
```

后台预览需要显式 `-StartHidden` 和正的 `-DurationSeconds`，例如 `30`。
可见预览必须另行显式选用 `-StartVisible`；本轮不执行该入口。可通过
`-RuntimeRoot` 指定隔离 runtime；缺省采用当前 global.json 指定 SDK 的
用户本地目录，不修改系统 PATH。启动前拒绝已有 Agent/Desktop 或当前
用户 pipe，数据与日志写入独立 run 目录，不安装服务、不启动 Broker，
也不执行系统动作。退出/中断仅清理本次持有的 Process 对象；强制结束
宿主 PowerShell 不能保证子进程清理。

脚本分别记录自然 Desktop 退出和 wrapper 清理，并在清理前记录 Agent
是否仍存活。限时预览由 wrapper 终止进程，不可把该终止写成产品的托盘
退出路径已通过。

## 已有程序覆盖与本轮增补

原 LifecycleTests 的 9 个用例覆盖关闭/Esc/最小化到托盘、恢复尺寸、退出
等待与资源释放、热键冲突、托盘缺失/失败回退、会话清理异常、系统注销
以及图标解码。两项真实 WPF/IPC IntegrationTests 还覆盖同一订阅、隐藏
恢复最新状态、异步查询中退出以及 Desktop 退出后 Agent 继续运行。
其中托盘/激活入口使用替身；原生热键用例只注册 Ctrl+Alt+Shift+F24 并
直接调用消息处理函数，没有按下真实 Ctrl+Alt+P。

新增 4 个定向用例验证参数与可见默认、隐藏启动 HWND 原生不可见且没有
显示/激活变化、隐藏启动托盘失败可完整清理、真实 IPC 隐藏启动单订阅
并通过已有托盘回调恢复最新状态。中间筛选结果为 `15/15`，0 失败、
0 跳过，命令和证据为：

```text
dotnet test tests/PerfMonitor.AgentTests/PerfMonitor.AgentTests.csproj --configuration Release --no-restore --artifacts-path artifacts/development-348a609-desktop --filter FullyQualifiedName~DesktopLifecycle --logger trx --results-directory artifacts/development-348a609-desktop/test-results --verbosity minimal
artifacts/development-348a609-desktop/validation/restore-locked.log
artifacts/development-348a609-desktop/validation/desktop-lifecycle-tests.log
```

该独立目录是中间定向验证，最终全量使用上述统一目录并另行记录。测试
窗口均使用 `Left/Top=-32000`、`ShowActivated=false`、无任务栏入口；
托盘与激活回调为替身，没有发送鼠标/键盘输入或抢占用户焦点。

## 当前显示环境与待验步骤

本轮只读显示枚举得到两个屏幕，不能沿用“单屏”假设：

| 显示器 | 像素边界 | 当前缩放 | 工作区 |
| --- | --- | --- | --- |
| DISPLAY1，主屏 | `(0,0)`，2048×1152 | 125% | 2048×1112 |
| DISPLAY2，副屏，竖屏 | `(-1440,-884)`，1440×2560 | 100% | 1440×2520 |

实际允许的物理验收范围可包含这两个现有缩放与跨屏移动。150%/200%、
其他设备及 Windows 11/Server 四目标矩阵仍需要相应环境。以下全部保持
待验，只有用户后续授权前台操作后再执行：

| 步骤 | 最小动作与预期 | 应记录的观察 |
| --- | --- | --- |
| 正常可见启动 | 明确启动同一绑定产物，看到紧凑连接态 | PID、四文件 hash、UTC、HWND、像素矩形、DPI |
| 默认热键 | 实际 Ctrl+Alt+P 隐藏/恢复；长按不反复闪烁 | 可见状态、前台 HWND、同一 PID、恢复数据时间 |
| 关闭与 Escape | 收起按钮及 Esc 均隐藏，Agent 保持采样 | 窗口状态、Agent 活性，未重复创建 Desktop |
| 真实托盘 | 左击与右键显示/收起菜单恢复同一窗口 | 菜单文字、图标、同一 HWND、窗口位置 |
| 展开恢复 | 网络/其他资源展开，隐藏后恢复 | 选中资源、1000×680 DIP 状态、最新质量/数据 |
| 焦点竞争 | 另一应用前台时从用户热键/托盘呼出 | 用户动作后前台窗口归属；后台启动无焦点变化 |
| 混合 DPI | 在现有 125%/100% 两屏移动、隐藏及恢复 | HWND DPI、像素尺寸、布局可读性、窗口可达 |
| 边界位置 | 靠近工作区右下缘展开/恢复 | 控件、退出入口是否仍可达；先复现再定性缺陷 |
| 真实退出 | 从托盘退出，随后再次启动 | Desktop 退出、图标移除/刷新、快捷键释放；wrapper 清理前 Agent 独立存活 |

截图或原始进程资料如包含个人应用名称，默认仅本地保留。观察记录应明确
区分后台隐藏启动、程序替身用例、真实窗口截图与物理输入，不能由一项
结果推断其他项已通过。
