# Wave09：本机用户安装入口

## 用户当前目标与授权

用户要亲自从普通用户视角体验本机安装、真实运行和卸载，不使用模拟数据，不要求运行测试文件，也不使用独立的预览数据目录。用户明确允许本次真实运行使用产品原有的 SHA-256 指纹；这一例外不授权额外文件哈希或发布清单哈希，产品指纹代码未更改。

版本仍为 1.1.0 开发候选，不创建发布标签。Wave07–08 的回归结果继续属于各自记录，不作为新安装入口的测试结果。

## 本轮交付

- `scripts/prepare-local-user-installer.ps1`：从现成 Release 输出复制 Agent、Worker、Desktop、Support 及既有 .NET 10.0.10 运行时；不构建、不执行测试、不计算文件哈希、不覆盖旧包。
- `scripts/local-install/Install-PerfMonitor.cmd` / `.ps1`：中文 WPF 当前用户安装界面。由用户点击安装，程序复制到 `%LOCALAPPDATA%\Programs\PerfMonitor`，创建桌面和开始菜单快捷方式，登记 HKCU 卸载入口。
- `Launch-PerfMonitor.vbs` / `.ps1`：隐藏启动辅助控制台，运行正式 Agent `--quiet` 与 Desktop；Agent 使用原有默认数据目录和正式当前用户 IPC。应用运行时随包携带，通过子进程环境指定，不依赖开发 SDK 路径，不改全局运行时。
- `Uninstall-PerfMonitor.cmd` / `.ps1`：卸载确认后只处理本安装路径的程序、快捷方式和注册项，保留 `%LOCALAPPDATA%\PerfMonitor` 数据与日志。删除前验证固定绝对路径、安装标记和重解析点。
- [用户说明](../local-user-install.md)明确启动、托盘生命周期、卸载及当前边界。

安装窗口有第二屏时把自身放到非主屏；没有发送鼠标或键盘输入。安装过程中不安装服务、不启动 Broker、不登记自启、不请求提权。Agent 与 Desktop 为当前用户的正常真实产品路径；软件启动后的硬件可读范围由实际环境决定。

## 原 MSI 的真实缺口

本机为 Windows 10 build 19045；现有 WiX MSI 客户端条件要求 build 26100–26999，因此不能直接安装本机。MSI 还依赖系统可发现的 .NET 10 Desktop Runtime，而本机全局只有 .NET 6；且安装完成后未衔接首次启动 Agent。没有找到现成 MSI、WiX 缓存或本次用户接受 WiX 7 EULA 的记录。

新增入口是实际的当前用户候选安装器，不是 WiX MSI，不能据此宣称原 MSI 的安装/升级/卸载生命周期通过，也不改变正式 OS 支持矩阵。Broker 特权动作和正式签名仍不在此次安装范围内。

## 文件与验证边界

候选包位于 `artifacts/local-user-installer-20261003`，不加入 Git。包内 `package-info.json` 记录各现成组件的产品版本和构建时间，不生成文件指纹。不同组件原有版本后缀不同，不宣称为新的统一发布构建。

只做了新 PowerShell 脚本的静态语法解析与代码审查，修正首装路径字符参数和卸载工作目录问题。没有重新构建、运行测试文件、重跑 CheckOnly/基线或既有 GUI 验收。安装和真实运行由用户自行体验；完整安装/卸载、正常 Agent/Worker 与实际温度仍不得提前记录为已通过。

已打开真实安装界面供用户操作：进程 PID `35172`，窗口标题“安装 PerfMonitor”，窗口句柄 `134080`，检查时响应正常、stderr 为空，尚无安装标记或产品进程。日志位于包根目录 `installer-ui.stdout.log` / `installer-ui.stderr.log`。此记录只证明安装入口已打开，不证明用户已完成安装或真实采集成功。

下一步优先处理用户此次实际安装与使用中发现的问题，再继续围绕选中进程、明确授权、原值保存及到期/冲突恢复推进性能优化。
