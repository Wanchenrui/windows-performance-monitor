# v1.0 支持矩阵

## 生产支持

| 操作系统 | 架构 | Agent/Desktop | Provider Worker | 可选 Broker | 安装包 |
|---|---|---:|---:|---:|---:|
| Windows 11 24H2 | x64 | 支持 | 支持 | 支持 | 支持 |
| Windows 11 25H2 | x64 | 支持 | 支持 | 支持 | 支持 |
| Windows Server 2022 Desktop Experience | x64 | 支持 | 支持，取决于硬件/权限 | 支持 | 支持 |
| Windows Server 2025 Desktop Experience | x64 | 支持 | 支持，取决于硬件/权限 | 支持 | 支持 |

“支持”表示整个已签名安装、运行、升级和卸载链路，而不是只表示某个
`.dll` 可以加载。Server Core 没有 Desktop UI，因此不列入 v1.0 Desktop
支持矩阵；MSI 同时读取 `InstallationType`，Server 目标只有值为
`Server`（Desktop Experience）时才允许安装，`Server Core` 会在
LaunchCondition 阶段阻止。

## 发布证据

`.github/workflows/support-matrix.yml` 只从受保护的 `main` 或
`v1.0.0` 运行，并要求四个专用 self-hosted runner：

| 目标 | Runner label | 强制主机身份 |
|---|---|---|
| Windows 11 24H2 | `perfmonitor-win11-24h2-x64` | build 26100、`Client`、24H2、x64 |
| Windows 11 25H2 | `perfmonitor-win11-25h2-x64` | build 26200、`Client`、25H2、x64 |
| Server 2022 Desktop | `perfmonitor-server2022-desktop-x64` | build 20348、`Server`、x64 |
| Server 2025 Desktop | `perfmonitor-server2025-desktop-x64` | build 26100、`Server`、x64 |

主机身份在调用 `msiexec` 前验证。四机必须消费同一个 MSI，并各自通过
安装、升级、降级阻止、repair、Broker 服务/ACL、卸载保留、回装和受控
回滚。聚合 JSON 必须符合
`contracts/v1/support-matrix-evidence-v1.schema.json` 并由
`actions/attest@v4` 建立来源证明。production 发布按 run ID 校验相同
commit 与 signer workflow，少一机即 fail closed。

## 明确不支持

| 平台 | v1.0 状态 | 原因 |
|---|---|---|
| x86 Windows | 不支持 | 产品、硬件 Worker 和 Broker 只冻结 x64 |
| ARM64 Windows | 不支持 | 尚无 Worker native asset、SQLite、MSI、签名和实机完整证据 |
| Windows 10 Home/Pro | 不支持 | 已于 2025-10-14 结束 Microsoft 支持 |
| Windows 11 23H2 Home/Pro | 不支持 | 已结束 Microsoft 支持 |
| Windows 7/8/8.1 | 不支持 | OS 与 .NET 10 均不在产品支持范围 |
| Wine/Proton | 不支持 | Windows ACL、Named Pipe identity、PDH、服务和签名语义不同 |

Windows 10 LTSC 或 Windows 11 Enterprise 的生命周期可能不同，但 v1.0
没有对应实机安装矩阵证据，因此不因 API 可用就推断为生产支持。

## 构建与验证环境

- SDK：`.NET SDK 10.0.302`
- Target framework：`net10.0-windows10.0.17763.0`
- Runtime identifier：`win-x64`
- Python oracle：CPython 3.12 x64，仅构建/差分使用
- Installer：WiX Toolset SDK 固定版本
- Signature：Authenticode SHA-256；production 要求 RFC 3161 时间戳

TFM 的最低 Windows API 版本不是支持承诺。GitHub `windows-latest` 只提供
其中一个 Server x64 环境；Windows 11 客户端矩阵必须由独立、受控的
签名候选测试记录补充，不能把 Server runner 结果伪装成客户端实机结果。
