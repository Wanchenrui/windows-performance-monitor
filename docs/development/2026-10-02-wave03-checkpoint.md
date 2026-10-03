# Wave03 开发检查点

**状态：WP01/WP04 本轮实现、测量与开发验证完成；仍为开发候选。**
最终结论由 v3 原始命令退出记录、日志、五份 TRX，以及另行完成的纯
CheckOnly 续记录共同组成。原 driver 的失败状态保留，未改写为成功。

## 起点与版本

本轮起点及当前 HEAD 为 `73ec33fd5c27e2658d5ce2c514388b71eb9a406b`，
本轮变更仍在 dirty 工作树。版本保持 `1.1.0` 开发候选，没有 commit、
push、tag 或发布。最终产物的版本后缀来自这个 HEAD，不能单凭后缀
证明 dirty 源码与产物对应。

上一轮 38 个 evidenceFiles 的先前核查记录保留在
[wave03 evidence audit](../../artifacts/wave03-evidence-audit/audit.json)。
既有清单、报告和原始证据不改写。历史 GitHub Actions run `37020967508`
属于 `73ec33f` 交接历史，不能验证本轮 dirty 改动。

## 本轮完成范围

- **WP01：**先保存快照路径的合成基线，再冻结载荷和集合所有权，复用
  未变化组，并让 Read 从同一原子版本轻量投影。`Data` 每次提供独立
  JsonNode 视图，增加只读入口并迁移已确认的关注进程投影热点。保留
  非有限缺测值的内存兼容路径，同时严格拒绝非法 JSON 序列化。四组
  微测量及 324000 原样本的既有离线复算记录保留；兼容 Data 的展开
  成本和实际组合路径的结果均如实记录。
- **WP04：**先复现五个不合作调用耗尽共享槽，再增加可选容量门，
  保持总并发上限，并给六个稳定基础组保留容量。排队超时使用已有
  `timeout`，空观测时间与零采集时长表示缺测，保持非重入、取消、
  回退、有限停止和延迟 Dispose。
- 新配对中旧四个基础组均 0 成功、最大间隔 3074.4277ms；最终各 31 次
  成功，最大间隔 110.6631–110.8236ms，低于预设 250ms。总调用峰值仍为
  5，单 Provider 峰值为 1，未结束调用未被超时释放来扩大正常运行并发。

详细方法、全部读数、代价和复现说明见
[wave03 measurements](2026-10-02-wave03-measurements.md)。WP01/WP04
实现与容量失败实际 schema 的交叉审查已互认关闭，无未关闭 P1/P2。

## 最终开发验证与保留的失败

实际最终构建输出为 `artifacts/wave03-final-v3-73ec33f`。隔离 SDK 为
`10.0.302`，Python 使用现有隔离环境；完整门禁结果为：

| 检查 | 实际结果 |
| --- | --- |
| solution locked restore | exit 0 |
| solution .NET 测试 | 五份 TRX，224/224，0 失败/跳过/错误/超时/中止 |
| Release build | exit 0，0 警告、0 错误 |
| pip check | exit 0，无依赖冲突 |
| Python 全量 | exit 0，76 passed in 3.46s |
| build-server shutdown | exit 0 |
| 追加纯 CheckOnly | exit 0，canLaunch=true，未启动产品进程 |

v3 原 `validation/final-verification-record.json` 的 `status=failed` 保留。
失败发生于全部六个验证命令退出 0 后，driver 没有识别 CRLF 的 pytest
汇总行；随后只补元数据解析自检，没有重跑成功命令。纯 CheckOnly
实际结果由 stdout 输出；旧 wrapper 期待 `preview-checkonly.log` 文件
是错误假设。原 stdout 和有效 `checkonly-continuation-v2.json` 构成续记。
同目录五字节 `checkonly-continuation.json` 是无效 `null` 元数据，分类为
RejectedArtifactWriter，排除于成功证据。

v2 Python 原件为 75 passed / 1 failed，失败原因是新增 PowerShell
wrapper 缺仓库要求的 UTF-8 BOM；它不是通过记录。BOM-only 修复后
实际 v3 全量重跑通过。v2 原 final record 未由失败 writer 生成，不虚构
补写。WP04 初版新增错误码不符封闭 v1 enum 的样本仍为 contract-invalid
早期候选；最终复用 `timeout`，真实容量失败完整快照和 baseGroup 均已
通过原 schema。WP01 初期 NaN 兼容失败、测试替身失败也由测量报告关联
到保留原件，未替换成成功日志。

## 新索引与最新证据限制

最终摘要为
[validation-summary.json](evidence/2026-10-02-wave03/validation-summary.json)，
原件路径、文件大小和分类为
[artifact-index.json](evidence/2026-10-02-wave03/artifact-index.json)。新索引
只引用本地保留原件，不复制全部候选输入或大 CSV。

用户最新指令禁止新增 SHA-256 计算。收到指令时没有仍在运行的本代理
归档计算进程；指令之后未再计算、复制或校验摘要值。已有历史记录
保持原状，新摘要和索引不包含 digest 字段。最终元数据仅依据已有
日志、退出码、计数、文件大小与 Git 差异，不宣称完成新的内容完整性
验证。验证期间及追加 CheckOnly 的 300 项输入稳定性属于原记录；本次
无摘要值检查仅确认这些路径存在、当前文件大小匹配及版本保持不变。

## 验收边界

本轮微测量不等于真实进程规模、全产品资源基线、产品预算或任务收益
验收。CheckOnly 不等于真实窗口、托盘、热键或物理输入验收。基础组
自身多个永久阻塞、sink 卡住及线程池耗尽仍能阻止采集，单槽配置没有
基础隔离保证。真实硬件/Worker 故障、受控全产品负载、72 小时长稳、
目标平台矩阵、动作恢复与物理交互仍待验。
