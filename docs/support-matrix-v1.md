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
支持矩阵。

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
