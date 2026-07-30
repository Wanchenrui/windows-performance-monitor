# ADR-012：签名发布、安装、更新与可恢复性

- 状态：Accepted
- 目标版本：1.0.0
- 日期：2026-07-30
- 实现状态：代码门禁与支持矩阵工作流已实现，真实 72 小时/四机矩阵/
  生产签名证据待完成

## 结论

1.0 只发布一个 `win-x64`、按机器安装的 MSI。核心功能安装到受保护的
`Program Files`，Agent 通过公共启动项在每个登录用户的非提权会话中运行，
Desktop 只连接同一用户 SID 的 Agent Pipe。Broker 是独立的可选 MSI
Feature；只有显式选择该 Feature 时才安装 LocalSystem 服务。

发布链路必须同时满足：

\[
\mathrm{ReleaseAllowed} =
\mathrm{Tests}
\land \mathrm{ResourceBudget}
\land \mathrm{Recovery}
\land \mathrm{VulnerabilityScan}
\land \mathrm{SBOM}
\land \mathrm{Authenticode}
\land \mathrm{UpdateManifestSignature}
\land \mathrm{SupportMatrix}
\land \mathrm{Provenance}
\]

任一项缺失都不能生成“production”候选。CI 使用临时自签证书验证签名机制，
但所有证据均标记为 `test`；测试证书不得通过生产发布门禁。

## 支持边界

v1.0 支持仍处于 Microsoft 支持周期内的 Windows 11 24H2/25H2 x64，
以及 Windows Server 2022/2025 x64。Windows 10 普通 Home/Pro 已于
2025-10-14 结束支持，不列入生产矩阵。ARM64 和 x86 在 v1.0 明确为
`not-supported`：Agent 的托管代码可编译并不等于硬件 Worker、SQLite
native asset、安装器和 Broker 的整条链路已经获得 ARM64 证据。

TFM 中的 `windows10.0.17763.0` 只是 API 最低绑定，不代表对已经结束支持的
操作系统作产品承诺。支持矩阵以 `docs/support-matrix-v1.md` 为准。

## 安装与权限

- MSI 为 per-machine x64 包；主程序目录由 Windows Installer 管理。
- `Core` Feature 包含 Agent、Desktop、Provider Worker、支持工具和用户
  启动/开始菜单快捷方式。
- `Broker` Feature 默认不安装。显式安装后创建
  `PerfMonitorBroker` 自动服务，账户固定为 LocalSystem，参数固定为
  `--service`。
- `%ProgramData%\PerfMonitor\broker` 只允许 LocalSystem 和本机
  Administrators 修改；普通用户不能替换机器策略或审计库。
- `%LOCALAPPDATA%\PerfMonitor\data` 仍归当前用户所有，不迁移到机器层。
- 卸载默认保留用户历史、策略、Broker 审计和 migration backup；数据删除
  必须是独立、显式的管理员操作。

安装包不使用开放命令行 custom action，不把用户输入拼接进高权限 shell。
服务、快捷方式、目录和 ACL 由声明式 MSI 表完成。WiX SDK 固定版本并纳入
依赖锁与 SBOM；接受工具许可证是构建环境的显式前置条件，不在脚本中暗中
代替用户接受。

## 签名分层

发布顺序固定为：

1. 发布所有 RID 固定的二进制，并生成明确不可发布的签名前 build
   manifest；
2. 对 Agent、Desktop、Provider Worker、Broker 和支持工具执行
   Authenticode SHA-256 签名；
3. 验证签名、证书 EKU、发布者、指纹和生产/测试模式；
4. 构建 MSI；
5. 对 MSI 执行 Authenticode SHA-256 签名并再次验证；
6. 运行安装、恢复、资源、隐私和 Broker 门禁；
7. 校验由指定 GitHub workflow 对同一 commit 生成并证明的四目标支持
   矩阵 evidence；
8. 生成 CycloneDX SBOM、漏洞报告和包含所有已签名文件 SHA-256 的
   final release manifest；
9. 生成 update manifest，并用同一发布者证书创建 detached CMS
   SHA-256 签名；
10. 在 GitHub 发布作业中为最终 MSI、update manifest、SBOM 和 final
   release manifest 建立
   artifact attestation。

生产模式要求 RFC 3161 时间戳、Code Signing EKU、非自签链以及配置的
发布者主体/指纹。PFX、密码和私钥不得写入仓库、日志、manifest 或 artifact。
测试模式生成短期自签证书，只用于证明签名和篡改检测代码可执行。

支持矩阵 evidence 必须精确包含 Windows 11 24H2 build 26100、Windows
11 25H2 build 26200、Windows Server 2022 build 20348 Desktop Experience
和 Windows Server 2025 build 26100 Desktop Experience。矩阵 workflow
在安装前校验主机，四台机器使用同一个 MSI；聚合结果单独 attested。
production 通过 run ID 下载它，并固定 signer workflow 与 source commit。
文档中的“支持”不能代替该机器证据。

## 更新清单

`update-manifest-v1.json` 是 UTF-8、属性顺序固定的 JSON，至少包含：

- schema/product/channel/version；
- published UTC、Git commit、DB schema；
- 支持的 OS/architecture；
- MSI URL、字节数和 SHA-256；
- SBOM URL 和 SHA-256；
- signer subject、SHA-256 certificate thumbprint、signature mode。

清单本体不携带签名字段。`update-manifest-v1.json.p7s` 是对原始字节的
detached CMS/PKCS#7 签名。验证必须同时检查：

\[
\mathrm{Valid} =
\mathrm{CmsSignatureValid}
\land \mathrm{ContentHashMatch}
\land \mathrm{PublisherPinned}
\land \mathrm{ThumbprintPinned}
\land \mathrm{CodeSigningEku}
\land \mathrm{ModeAllowed}
\]

只验证“某个证书签过”不构成更新信任。任何字节篡改、发布者/指纹不匹配、
测试证书用于生产或 artifact 哈希不匹配都 fail closed。

## 数据库 migration 与回滚

用户历史库和 Broker 审计库不与安装目录混放。每次 schema 升级遵守：

1. 停止写入；
2. `PRAGMA integrity_check(1)`；
3. 通过 SQLite online backup API 生成同卷临时 backup；
4. 校验 backup 完整性、schema version 和 SHA-256；
5. 原子重命名为不可变的 pre-migration backup；
6. 在单一事务内执行向前 migration；
7. 再次执行 integrity check 后才启动新版本。

migration 事务失败时数据库保持旧 schema，backup 保留。新版本启动后发生
业务故障时，不自动用旧数据覆盖新写入；受控回滚先导出诊断证据，再卸载
当前 MSI、安装上一份已签名 MSI，并且只在管理员确认会丢弃升级后数据时，
用哈希和 schema 均匹配的 backup 恢复。

Support 恢复还必须持有与 Agent 相同的 `.agent.lock` 排他锁，校验 manifest
中的源文件名与目标库一致，并在同卷验证临时副本后原子替换。Agent 未停止、
WAL/SHM 仍存在、目标错配或最终复验失败时一律拒绝。

v1.0 不新增用户数据库 schema，当前版本仍为 schema 2，因此回退到
v0.7.2 不需要数据变换。门禁仍对 schema 1 -> 2 backup、失败回滚、WAL
异常终止恢复和“新 schema 拒绝旧二进制写入”进行测试，为后续 migration
冻结机制。

## 资源预算

发布 Agent 的稳态默认门限为：

| 资源 | 门限 |
|---|---:|
| working set peak | 256 MiB |
| private bytes peak | 256 MiB |
| retained private growth | 32 MiB |
| GC heap growth | 16 MiB |
| mean CPU, one logical core equivalent | 20% |
| handles peak | 768 |
| retained handle growth | 64 |
| threads | 64 |

这些是软件门禁，不是硬件物理极限。句柄峰值在含 384 个系统进程的本地
目标机上实测为 567；其中 Agent 无 Worker 为 486、启用 Worker/IPC 后稳态
约 499～530。峰值因此按实测值加 25% 余量后向上取 256 边界为 768，同时
新增首尾 20% 中位数差不超过 64 的增长门禁，避免放宽峰值后掩盖句柄泄漏。
短 CI gate 与真实 72 小时 gate 使用同一计算公式；72 小时证据必须按真实
墙钟通过，不能由虚拟时间代替。Provider 超时和 Broker 请求不占用同一
调度槽，新增签名/更新/诊断代码不进入采样热路径。

生产发布不能只检查 72 小时证据的布尔结果。仓库内冻结的 baseline
candidate 描述同时固定 v0.4 产品版本、源 head、CI run/artifact digest、
Agent DLL/入口 SHA-256 和验证器 SHA-256。发布门禁重新计算资源阈值，
要求 UTC 起止跨度、进程完整退出和采样覆盖都达到 72 小时，并拒绝完成
时间仍在未来的证据；baseline head 还必须是当前发布 commit 的祖先。
冻结的 300 秒快照周期和 30 秒资源探针用于推导最低快照与稳态资源样本
覆盖量；候选或证据任一缺失、错配、稀疏都 fail closed。

## 隐私与诊断

产品默认只在本机保存监控、历史和诊断数据，不自动上传。默认诊断包采用
allowlist：只输出版本、schema、稳定 provider/error ID、健康计数、资源
预算结果和已经脱敏的事件摘要。默认排除：

- 用户名、SID、机器名和 IP/MAC；
- 进程 PID/名称/路径/命令行；
- 原始 snapshot JSON 和 SQLite 数据库；
- 传感器/设备原始名称；
- Broker 客户端映像路径/哈希和完整审计 payload；
- 策略文件、密钥、证书私钥和环境变量。

字符串脱敏不是最后一道防线；诊断导出器必须从结构化 allowlist 构造新
对象，随后再执行模式扫描并在命中敏感模式时拒绝生成包。

## Broker 安全发布门禁

Broker 不只依赖请求体或可伪造的路径。服务端从实际 Named Pipe 连接取得
客户端 SID/PID，打开该进程映像并在禁止写/删除共享的文件句柄存续期间：

1. 用 `GetFinalPathNameByHandle` 得到重解析后的最终路径；
2. 计算完整文件 SHA-256；
3. 以 `WinVerifyTrust/WINTRUST_ACTION_GENERIC_VERIFY_V2` 验证 Authenticode
   内容和本机信任链；
4. 从已验证 provider state 取得 signer，要求显式 Code Signing EKU；
5. 与机器策略中的最终 Program Files 路径、文件 hash、Signer Subject 和
   DER 证书 SHA-256 同时比较。

service 模式强制开启上述门禁，配置文件不能将其降级。校验禁止网络 URL
retrieval，避免证书网络查询阻塞 Broker 连接；生产候选的证书吊销/禁用仍由
发布环境、签名更新清单和升级策略负责。

除既有单元测试外，必须对真实 Broker Pipe 运行畸形 framing、超大长度、
截断 JSON、未知 operation/action、身份字段注入、幂等冲突、并发重放和
连接中止。fuzz 输入不得触发动作 executor，服务应继续接受后续合法请求。

生产服务安装验证还必须确认：

- 服务账户为 LocalSystem 且二进制路径位于固定 Program Files；
- Broker Feature 缺失时没有服务、Pipe 或 Broker 文件泄漏；
- ProgramData policy/audit ACL 不能被普通用户写入；
- 安装包和所有可执行入口的 signer 与 update manifest 绑定。

## 回退

代码回退：revert v1.0 分支提交。部署回退：保存诊断证据，停止 Agent 和
Broker，卸载 1.0 MSI，安装上一份已验证签名的 MSI；数据库保持原位。只有
schema migration 已发生且管理员明确接受丢失新版本数据时才恢复
pre-migration backup。
