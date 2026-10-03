# 版本更新前的开发验收证据归档

本目录保存 2026-10-02 版本更新前已验证候选的部分原始证据，属于基线提交 `74d29ddfc8a29fac23af5d57068b2aef08c42804` 上的未提交实现。它不是 1.1.0 二进制或新提交的重新验收结论。旧 WAL 记录中的产品版本为 `1.0.0`；版本更新后的证据必须另行命名，不覆盖这些文件。

完整历史哈希索引的逐字节副本为 [`../../2026-10-02-pre-version-artifact-hashes.json`](../../2026-10-02-pre-version-artifact-hashes.json)，原始文件 SHA-256 为 `9685F8B82C006A3A3C4C6B74A743079BC6BC155244630ECFC1BF7FB7BAA0C0B1`。该索引中的路径仍指向本地原始位置，不能据此认为所有产物已上传。

[`archive-index.json`](archive-index.json) 列出 20 个归档文件的原始路径、提交路径、字节数、SHA-256，以及是否列入旧索引。文件均为原始字节副本；根目录 `.gitattributes` 的精确 `-text` 规则防止 Git 换行转换改变归档哈希。

保留的程序验证证据包括 locked restore、Release build、全量 .NET 测试、五份 TRX、CPython 测试、WAL 异常终止/重启、锁文件审计，以及预览入口 `-CheckOnly` 日志。.NET 五份 TRX 合计 204/204，0 失败、0 未执行；Python 日志为 76/76；Release build 为 0 警告、0 错误。WAL 日志保留环境启动失败与后续成功重试，不隐去失败尝试。

归档的四张窗口截图覆盖真实 Desktop 的等待、连接、断连历史状态，以及 integration-host 的网络展开布局；均已目检，不包含个人应用名称或私人文本。`capture-evidence.json` 和 `layout-host-evidence.json` 保留采集参数、绑定产物哈希、窗口尺寸和 DPI。布局宿主没有启动 DesktopProgram 的托盘/热键集成，其截图不能证明这些行为已通过。原 layout-host JSON 也记录了未归档的进程抽屉截图哈希；该文件仍留在本地 `artifacts/validation/ui-screenshots/final-hud-layout/host-03-process-drawer.png`，因包含真实个人应用名称而没有上传。

M0 的 `resources.csv` 与 `result.json` 仅记录短时 Agent 进程资源和退出结果，不含本机进程清单。这两份文件未列入旧完整索引，归档索引单独记录其 SHA-256；不能由它们推导新版本产物绑定、全产品稳态表现或资源门限已经达标。

数据库、WAL/SHM、完整快照/stdout、产品二进制、测试宿主 bin/obj、SDK/runtime 和大型供应商元数据保持本地。原开发预览入口 `artifacts/preview/Start-Preview.ps1` 同样仅本地保留；归档的 CheckOnly 日志是历史验证记录。

本机为 Windows 10 开发环境，不属于仓库四目标支持矩阵。上述记录没有覆盖物理托盘、热键、焦点竞争、混合 DPI、多屏、安装部署矩阵、真实动作或 72 小时验收。原验收正文保留在 [`../../2026-10-02-validation.md`](../../2026-10-02-validation.md)。
