# 1.1.0 隔离程序级验证证据

本目录记录 2026-10-02 对 `1.1.0` 开发/测试候选重新执行的 .NET 验证。
构建时 HEAD 为 `74d29ddfc8a29fac23af5d57068b2aef08c42804`，工作树
包含未提交实现及版本调整。证据不表示后来提交的精确二进制已重建，也
不构成正式发布验收。

[验证摘要](validation-summary.json) 保存命令主体、退出码、项目/测试
统计、环境、版本读数及未验证范围。命令统一使用
`--artifacts-path artifacts/version-1.1.0`；测试还将 TRX 写入该目录的
`test-results/`。本机为 Windows 10 22H2 build 19045 x64，隔离 SDK
为 `10.0.302`，不属于仓库四目标支持矩阵。

| 验证 | 结果 | 归档日志 |
| --- | --- | --- |
| locked restore | 21 个项目成功，退出码 0 | [restore-locked.log](logs/restore-locked.log) |
| Release 全量 .NET 测试 | 204/204 通过，0 失败、0 跳过，退出码 0 | [test-release.log](logs/test-release.log) |
| Release 全解决方案构建 | 21 个项目通过，0 警告、0 错误，退出码 0 | [build-release.log](logs/build-release.log) |

五份原样 TRX 保存在 `test-results/`，分项为 Contract 11、Actions 33、
Broker 27、Agent 123、Release 10。三份日志与五份 TRX 均按 SHA-256
与本地原件核对，8/8 字节一致；证据目录的 `.gitattributes` 规则保留
原始字节。

[产物与证据哈希清单](artifact-hashes.json) 记录五产品 Agent、Broker、
Desktop、ProviderWorker、Support 的十个 EXE/DLL 文件、五个输出目录
指纹和上述八份归档证据。全部产品文件的 `FileVersion` 为 `1.1.0.0`，
`ProductVersion` 为 `1.1.0+74d29ddfc8a29fac23af5d57068b2aef08c42804`。
目录指纹按 UTF-8 编码、ordinal 排序的 `相对路径|大写文件 SHA-256`
行计算，每行以 LF 结束。产品二进制与完整 bin/obj 目录保持本地，不在
本源码归档中。

历史 manifest 记录的 47 份文件与五个原产品输出目录指纹仍与原值匹配。
旧验收正文和 manifest 哈希也未改变；详情见
[历史证据归档](../2026-10-02-pre-version/README.md) 和
[本轮开发检查点](../../2026-10-02-v1.1.0-checkpoint.md)。

本轮没有重跑 Python、新版 WAL 异常终止/重启或新版 UI 截图，也未执行
物理托盘/热键、焦点竞争、多 DPI/多屏、MSI/签名/安装、Windows 11/Server
矩阵、真实 72 小时或真实特权动作/恢复验收。此前 Python 76/76 和有限
UI/WAL 记录继续归于升号前证据，不能据此扩大 `1.1.0` 的验收范围。
