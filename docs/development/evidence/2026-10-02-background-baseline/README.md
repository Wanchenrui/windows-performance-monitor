# 本轮后台开发证据

本目录保留版本 `1.1.0`、基线 HEAD
`348a6091c5eb10b55baa87850263df0eae306634` 的冻结 dirty 工作树证据。
它不表示后续提交的二进制已重建，也不是发布、稳态预算或完整性能验收。

精确来源、大小和 SHA-256 集中记录在[哈希清单](artifact-hashes.json)；
测试及两组正式样本记录在[摘要](validation-summary.json)。本轮解释与未验
范围见[后台开发记录](../../2026-10-02-background-baseline.md)。

| 目录 | 用途与原件范围 |
| --- | --- |
| `validation/` | 统一 Release restore/build/test 日志、五份 TRX；数值修复后最后 Python 76/76、pip check 与冻结输入记录 |
| `tool-validation/` | PowerShell 5.1/7 数值回归及实际脚本 AST 检查；离线审核脚本 |
| `tool-validation/agent-toolcheck-invalid/` | 第一次 60 秒无效工具自检，JSON 空键及整数 CPU 舍入错误原件，不能用于性能结论 |
| `tool-validation/agent-toolcheck-v2/` | 修复后的 60 秒工具验证，仅用于放行正式样本 |
| `samples/agent-worker-180s/` | 第一组正式后台短样本：Agent＋所属 Worker |
| `samples/agent-worker-hidden-180s/` | 第二组正式后台短样本：Agent＋所属 Worker＋隐藏 Desktop |

每个 run 子目录只保存 `baseline.json`、两个自有产品资源 CSV、驱动日志
和必要的独立审核结果。无效工具自检的空 JSON 属性名按原字节保留，不能
用默认 `ConvertFrom-Json` 读取；修复后的 run 必须可默认解析。CPU 使用
实际有效 interval 的时间加权均值；私有内存 P95 如有派生，按测量点的
nearest-rank `ceil(0.95*n)` 定义，区别于采样器记录的峰值。

原件中的本机 OS/CPU、账户/仓库路径、机器名、PID 和构建环境属于开发
测量元数据。已核对 CSV 只含本轮自有产品资源，历史检查只含分组质量
计数，不包含其他应用名称、设备 ID 或诊断值。数据库、payload、完整
bin/obj、产品 stdout/stderr、截图留在本地，不纳入提交。精准属性规则
保留归档的 CRLF 与其他原始字节。

离线工具保存本次实际验证步骤，其本地路径指向原始工作区与本地原件。
离线重算用于验证统计链，不能替代实际新运行。旧 1.0.0、上一轮
1.1.0、15/15 中间筛选与 `development-348a609-desktop` 不并入本轮正式
构建或性能样本。

39 个归档文件包含 36 个原样证据及 README/摘要/清单三个元数据文件。
两正式样本均通过独立 CSV→摘要/哈希/只读历史复核，分别为 179 个有效
CPU interval；它们是本轮唯一正式数值样本。工具失败和修复后 60 秒仅
说明工具可信性，不能并入正式性能数值。

Desktop 退出 -1 来自 wrapper 测量后终止；Worker 退出 -1 来自 Agent
结束时主动回收。`already exited` 只表示 finally 清理时状态，不等于
自然成功退出或真实托盘退出验收。隐藏样本未见可见窗口，不覆盖物理
托盘/热键/焦点或混合 DPI/多屏；两组串行短测未控制用户负载，不能作为
稳态预算、72 小时、平台矩阵或 Desktop 因果增量结论。
