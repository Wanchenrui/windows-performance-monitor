# PerfMonitor v1.0 发布操作手册

## 结论

`.github/workflows/release.yml` 是 v1.0 唯一的候选打包入口。`test` 模式
验证完整机制，但不可发布；只有受信生产证书、真实时间戳、支持矩阵、
真实 72 小时墙钟证据和 GitHub attestation 全部满足后，`production`
模式生成的候选才可能进入正式发布。

WiX 7.0.0 采用 OSMF EULA v1.1。操作者必须先自行阅读
<https://docs.firegiant.com/wix/osmf/>、确认适用义务并完成所需安排。仓库
不会替操作者接受：MSI 项目不保存 `AcceptEula`，本地脚本和 GitHub
workflow 都要求本次调用显式提供 `wix7`。

## 生产 Secrets

在受保护的 GitHub Environment/Repository Secrets 配置：

| Secret | 内容 | 约束 |
|---|---|---|
| `PERFMONITOR_SIGNING_PFX_BASE64` | PFX 原始字节的 Base64 | 不带 PEM 头；不得写入 artifact |
| `PERFMONITOR_SIGNING_PFX_PASSWORD` | PFX 密码 | 仅运行期内存使用 |
| `PERFMONITOR_SIGNING_SUBJECT` | 证书完整 Subject | 与证书属性逐字节一致 |
| `PERFMONITOR_SIGNING_CERT_SHA256` | DER 证书 SHA-256 | 64 位十六进制 |
| `PERFMONITOR_TIMESTAMP_URL` | RFC 3161 服务 | 必须为 HTTPS |

生产证书还必须满足：

- 当前有效并含 Code Signing EKU `1.3.6.1.5.5.7.3.3`；
- 非自签，且链在 runner 的 Windows 信任策略中有效；
- 私钥可用于 SignTool，但 PFX 不允许作为构建 artifact 导出；
- Subject 和 SHA-256 pin 同时匹配，不能只依赖 SHA-1 store lookup。

生产 workflow 只允许从 `main` 或精确 `v1.0.0` tag 启动。建议把
`production` GitHub Environment 设置为至少一名独立审查人，并限制只有
维护者可读取上述 Secrets。

## Test candidate

1. 在 GitHub Actions 选择 **v1 Release Candidate**。
2. 选择目标分支，`mode=test`。
3. 仅在已审阅并接受 WiX 条款后，把 `wix_eula_id` 填为 `wix7`。
4. test URL 可保持 `artifact://...`；启动工作流。
5. 确认临时证书在作业末尾被删除，artifact 中没有 `.pfx`/`.cer`。

测试模式创建四小时有效的 RSA-3072 自签 Code Signing 证书，并临时加入
当前 runner 的 Root/TrustedPublisher。所有签名证据带
`signatureMode=test`，`candidateEligible=false`。

production PFX 和密码只注入证书导入步骤；完整 Python/.NET 测试在生产
私钥导入前完成。导入后证书不可导出，并由 `always()` 清理步骤移除。

## Production candidate

1. 确认所有堆叠版本已按顺序合并、`main` 与 GitHub 完全同步且工作树干净。
2. 确认 v0.4 基线真实 72 小时证据
   `release72HourGate.actualWallClockPassed=true`，并保存证据哈希。证据的
   `productVersion` 和 `agentSha256` 必须与
   `release/evidence/v0.4-agent-baseline-candidate.json` 一致；不要手工
   修改证据来满足门禁。
3. 为四台隔离实验机配置下列 self-hosted runner label，并确保 runner
   以管理员身份运行、没有预装 PerfMonitor：
   `perfmonitor-win11-24h2-x64`、`perfmonitor-win11-25h2-x64`、
   `perfmonitor-server2022-desktop-x64`、
   `perfmonitor-server2025-desktop-x64`。
4. 从同一个 `main` commit 运行 **v1 Windows Support Matrix**。在已接受
   WiX 条款后显式输入 `wix7`。该工作流只构建一次 test-signed MSI，
   在任何安装写入前核对每台机器 build、x64 和 `InstallationType`，再把
   同一 MSI 分发到四台机器。保存成功的 run ID。
5. 配置并复核五个生产 Secrets。
6. 运行 **v1 Release Candidate**：`mode=production`，填入上述
   `support_matrix_run_id`、最终 MSI/SBOM HTTPS URL，并再次显式输入
   `wix7`。发布工作流会下载聚合 evidence，校验成功 run、相同 commit、
   指定 signer workflow 和 GitHub attestation；不能上传本地 JSON 代替。
7. 运行成功后记录 workflow run ID、commit SHA、artifact ID、attestation
   URL/digest、MSI SHA-256、证书 SHA-256 和时间戳验证结果。
8. 只从该精确 commit 建立 `v1.0.0` annotated tag；不得重建或替换已证明
   的文件。

## 自动门禁顺序

```text
locked restore + Python/.NET tests
  -> publish win-x64 payload
  -> sign and verify five executable entry points
  -> build/sign synthetic 0.9 MSI and current 1.0 MSI
  -> inspect MSI tables and signer
  -> install/upgrade/downgrade/uninstall/reinstall/rollback
  -> smoke + forced WAL crash + resource budget
  -> NuGet/pip-audit vulnerability gate
  -> CycloneDX 1.6 SBOM including final signed MSI
  -> detached-CMS update manifest + verification
  -> release manifest + JSON Schema validation
  -> GitHub OIDC artifact attestation
```

production 流水线之前另有：

```text
one test-signed MSI
  -> four exact Windows targets
  -> pre-mutation host/build/Desktop Experience check
  -> full lifecycle on every target
  -> aggregate + attest support-matrix evidence
  -> bind run/workflow/commit in production
```

synthetic 0.9 MSI 使用相同 payload、仅改变 MSI ProductVersion，用于验证
Windows Installer 的 MajorUpgrade、直接降级阻止、repair、卸载和回装
状态机；它不是伪装成历史发布二进制。数据库前向 migration、旧 schema
保持和受控恢复由独立恢复门禁验证。

漏洞扫描失败、advisory 数据不可解析、豁免过期或任一未豁免 finding 都会
阻止候选。初始 `release/vulnerability-waivers-v1.json` 为空；新增豁免必须
包含 ecosystem、package、advisory ID、owner、影响分析、审查引用和 UTC
到期时间。

三个 `requirements*.txt` 不是只有版本号的清单：它们固定 Windows x64、
CPython 3.12 的每个 direct/transitive wheel SHA-256。`setup.ps1` 和审计
工具 bootstrap 强制 `--require-hashes --only-binary=:all:`；哈希不符、
缺少传递依赖 pin 或只能取得 sdist 时立即失败。

## 本地复现

先安装仓库固定的 .NET SDK 10.0.302 与 x64 Python 3.12，然后：

```powershell
.\scripts\setup.ps1
.\scripts\build.ps1
.\scripts\smoke.ps1
.\scripts\test_crash_recovery.ps1 `
  -AgentPath .\dist\agent\perf-monitor-agent.exe `
  -SupportPath .\dist\support\perf-monitor-support.exe
.\scripts\test_vulnerabilities.ps1 -BootstrapAuditTool
.\scripts\new_sbom.ps1
```

这部分不需要接受 WiX 条款。只有实际还原/构建安装项目时，在完成许可审阅
后才执行：

```powershell
.\scripts\build_installer.ps1 `
  -Mode test `
  -WixEulaId wix7 `
  -Publisher "CN=PerfMonitor CI Test"
```

生产私钥不应放到开发机脚本参数、PowerShell history、仓库目录或本地日志。

## 回滚

1. 导出默认脱敏诊断并记录当前 MSI/update manifest/SBOM 哈希。
2. 验证上一候选的 Authenticode、发布者 pin 和 artifact attestation。
3. 停止 Agent 与 Broker，卸载当前 MSI；不删除 LocalAppData/ProgramData。
4. 安装上一份 MSI，验证 Agent、Desktop、SQLite 和可选 Broker。
5. 只有发生 schema migration 且管理员确认接受丢失升级后数据时，才用
   Support 工具恢复 hash、schema 和 `integrity_check` 均通过的
   pre-migration backup。

自动用旧数据库覆盖新写入不属于允许的回滚策略。
若 `.agent.lock` 无法独占，或数据库仍有 `-wal`/`-shm`，Support 会拒绝
恢复；不得通过强删 sidecar 绕过，应先让 SQLite 正常恢复/检查并完成干净
停机。
