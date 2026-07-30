# v1.0 隐私说明与诊断包脱敏

## 默认行为

PerfMonitor 1.0 是本地性能监控工具。默认不建立遥测上传端点，不自动上传
快照、诊断、进程信息、设备信息、策略或 Broker 审计。Desktop、Agent、
Provider Worker 和 Broker 之间只使用本机进程间通信。

## 本机数据

| 数据 | 用途 | 默认存储 |
|---|---|---|
| CPU、内存、网络/磁盘速率、电源、容量、uptime | 实时/历史趋势 | 用户 SQLite |
| 进程名、PID、CPU/内存、创建时间 | 当前用户进程诊断/动作目标 | 实时快照和有界历史 |
| GPU/温度读数与会话内设备 ID | 硬件状态 | 实时；只保存聚合历史 |
| 确定性诊断事件 | 本地故障解释 | 用户 SQLite |
| 动作 caller/image hash、前后状态和结果 | 特权动作安全审计 | Broker 独立 SQLite |
| 用户/机器策略 | 本地配置 | 用户 LocalAppData / 机器 ProgramData |

产品不采集键盘输入、剪贴板、文件内容、浏览器历史、网络包内容或进程
命令行。进程名和设备名仍可能揭示用户行为或硬件信息，因此不默认进入
可分享的诊断包。

## 默认诊断包 allowlist

诊断导出器从以下结构化字段重新构造对象，不复制原始数据库或 snapshot：

- product/contract/schema 版本；
- OS build 和 architecture，不含机器名、用户名或序列号；
- Agent/Broker/Worker 是否存在及稳定健康状态；
- provider ID、quality、stable error code 和聚合失败计数；
- SQLite 大小、integrity 结果、行数和时间范围；
- 资源预算的 peak/growth/limit/pass；
- 已脱敏诊断规则 ID、severity/state 和时间桶；
- build manifest、SBOM 和签名验证摘要。

默认不导出：

- SID、用户名、机器名、域、IP/MAC；
- PID、进程/窗口名称、路径、命令行；
- 原始 snapshot、数据库、policy、环境变量；
- 设备/传感器原始名称或稳定硬件标识；
- Broker image path/hash、完整动作参数或审计 payload；
- PFX、私钥、密码、token、证书存储内容。

导出完成前还要扫描 Windows 路径、SID、IPv4/IPv6、MAC、电子邮件和常见
secret 形式；命中时拒绝打包并输出稳定错误码。扫描是防御补充，不能替代
allowlist。

## 分享与保留

诊断包只在用户明确执行导出时生成。工具必须先显示输出路径和包含类别；
本项目不自动发送。分享前用户仍应按组织的数据分类要求复核。删除诊断包
不会删除本机历史；卸载默认也保留历史和安全审计，避免在事故处理中丢失
证据。需要删除本机数据时应停止相关进程后，由用户/管理员显式删除对应的
LocalAppData 或 ProgramData 目录。

## 安全边界

脱敏不降低 Broker 审计的本机完整性要求。Broker 审计用于安全追责，原始
库不进入普通诊断包；若安全事件调查确需导出，必须走独立管理员流程并把
该文件视为敏感数据。
