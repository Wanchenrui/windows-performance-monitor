# ADR-009：稳定 Windows 扩展指标的物理口径

- 状态：Accepted
- 目标版本：0.7.0

## 决策

v0.7.0 只在 Agent 进程内增加具有稳定、非提权 Windows/.NET 接口的
网络、磁盘 I/O 和电源/电池 Provider。它们继续遵守独立绝对期限、超时、
同 Provider 不重入、有界并发和局部失败退避。

厂商 GPU、温度和传感器 SDK 不属于这个信任域，必须进入后续独立 Worker。
本版本不新增 Broker 或任何系统修改动作。

## 网络吞吐

对当前与上一样本都存在、且累计计数未回退的接口集合 \(I_k\)，使用
单调时间差 \(\Delta t_k\)：

\[
R_{\mathrm{rx},k} =
\frac{\sum_{i\in I_k}(C_{\mathrm{rx},i,k}-C_{\mathrm{rx},i,k-1})}
{\Delta t_k}
\]

\[
R_{\mathrm{tx},k} =
\frac{\sum_{i\in I_k}(C_{\mathrm{tx},i,k}-C_{\mathrm{tx},i,k-1})}
{\Delta t_k}
\]

累计字节来自 `.NET NetworkInterface.GetIPStatistics()`。只纳入运行状态为
Up 的非回环接口。新接口只建立基线；计数器回退表示复位，该接口当前速率
无效并记录 `invalid_data`，不得用负值或巨大无符号差制造尖峰。

该汇总可能同时包含虚拟隧道与底层网卡，语义固定为“活动网卡计数器总和”，
不宣称是物理链路或公网流量的去重值。接口 ID 只用于 RAM 差分，不写入
SQLite 或诊断事件。

## 磁盘 I/O

使用 PDH 语言无关英文路径：

```text
\PhysicalDisk(_Total)\Disk Read Bytes/sec
\PhysicalDisk(_Total)\Disk Write Bytes/sec
\PhysicalDisk(_Total)\Disk Reads/sec
\PhysicalDisk(_Total)\Disk Writes/sec
```

PDH 速率计数器需要至少两个采样。第一样本 `sampleReady=false`，四个速率
均为 `null`。第二样本起只接受状态有效、有限且非负的值。PDH 查询句柄是
Provider 运行状态，调度器先停止所有采集任务，再释放句柄。

计数器对象不存在或被禁用时 `diskIo` 局部降级；不得回退到 WMI、
PowerShell、外部命令或伪造 0。

## 电源与电池

`GetSystemPowerStatus` 的字段按 Microsoft 定义映射：

- `ACLineStatus`: 0=电池、1=交流电、255=未知；
- `BatteryFlag & 128`: 无系统电池；
- `BatteryFlag & 8`: 正在充电；
- `BatteryLifePercent`: 0～100 有效，255=未知；
- `BatteryLifeTime/BatteryFullLifeTime`: `UINT_MAX`=未知；
- `SystemStatusFlag`: 0/1=节能关闭/开启。

无电池是有效系统状态，表示为 `batteryPresent=false`；未知数值保持
`null`。这两个状态都不能表示为 0% 或 0 秒。

## 时序与持久化

| Provider | 周期 | 超时 | 持久化 |
|---|---:|---:|---|
| network | 1 s | 750 ms | 收/发速率 |
| diskIo | 1 s | 750 ms | 字节与操作速率 |
| power | 5 s | 500 ms | 电量百分比 |

Provider 调用发生在现有隔离任务中；SQLite 仍只接收不可阻塞的快照副本。
新增指标不会引入第二个 writer，也不会改变 schema v2。

## 回退

v0.7.0 只增加指标 ID 和普通 metric 行，不执行数据库 migration。回退
v0.6.0 时旧 Agent 忽略不认识的新指标，历史数据库保留。
