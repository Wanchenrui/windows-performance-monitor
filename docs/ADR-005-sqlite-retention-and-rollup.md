# ADR-005：SQLite、保留与聚合策略

- 状态：已采用
- 日期：2026-07-29
- 目标实现：0.5.0
- 实现状态：0.5.0 已实现

## 数据流

SQLite 是快照发布后的异步消费者，不在采样线程同步写入：

```text
Provider Results
  -> Snapshot Assembler
     -> Atomic Latest Snapshot
     -> bounded channel -> SQLite Writer
     -> bounded channel -> Diagnostic Engine
     -> latest-wins channel -> IPC subscribers
```

队列必须有界。存储落后时增加 `droppedPersistenceSamples` 并保留实时
采集，不允许反压破坏 Provider 时序。

## SQLite 规则

- 单写线程、批量事务、WAL；
- 查询与写入使用不同连接；
- `busy_timeout` 有限，禁止无限等待；
- 所有 schema 变化使用带版本号的显式 migration；
- 启动时执行完整性检查和崩溃恢复测试；
- 原始指标、rollup、诊断事件、动作审计分表。

## 保留

- 系统级 1 秒原始样本短期保存，再生成分钟/小时 rollup；
- rollup 至少保存 `min/max/avg/last/count`；
- 默认不持久化 Top-N 或全进程明细；显式关注名称只保存规则需要的 CPU
  最大值、实例集合哈希和完整性状态，与 raw 同样保留 48 小时；
- 本轮不持久化进程生命周期明细；
- 命令行和完整路径默认不采集、不持久化。

历史查询必须包含时间范围和 `maxPoints`，服务端聚合保留尖峰。migration
失败时数据库以只读/新文件降级，实时快照仍可用；不得静默删除用户历史。

## 0.5 实现参数

- 数据库：`%LOCALAPPDATA%\PerfMonitor\data\history-v1.db`；
- schema：`PRAGMA user_version=1`，migration 记录写入
  `schema_migrations`；
- 写队列：容量 256，单 reader，批量上限 64；生产者只调用
  `TryWrite`，不等待；
- SQLite：WAL、`synchronous=NORMAL`、1 秒 `busy_timeout`、连接池关闭，
  读写连接分离；
- 启动：先执行 `integrity_check(1)`，再应用显式 migration，最后执行
  被动 WAL checkpoint；
- 保留：raw 48 小时、分钟 rollup 30 天、小时 rollup 366 天；
- 查询：最多 16 个白名单指标、5,000 个输出桶、366 天；在 SQL 中计算
  `min/max/avg/last`。

初始化失败时状态进入 `DegradedReadOnly`；已有数据库仍可尝试只读查询。
运行中写失败会停止 writer、清空有界队列并累计
`droppedPersistenceSamples`/`writeFailures`，但不取消 Provider scheduler
或原子实时快照。v1 不持久化完整路径、命令行或任意脚本文本。

## schema v3 持久化边界

`snapshots_raw` 是版本化最小回放投影，不再是完整实时快照。历史白名单标量、
系统卷利用率、所有组质量与单调时间，以及显式 watchlist 的名称 CPU 聚合
构成允许内容；硬件 devices/sensors、卷路径、PID、创建时间和全进程列表
不入新行。默认 watchlist 为空。规则输入聚合由 Diagnostics 的共享窄入口
提供，Storage 不执行诊断。

`metrics_raw` 保存 unit/source、质量、真实观测 UTC、单调经过时间和组观测
序号；以实例、组、指标、观测身份去重。只有新鲜有效观测进入 rollup，缺测
保存 null 而不作为 0 或重复样本。数量和 UTF-8 序列化载荷字节双限：每投影 64 KiB、
每事件 128 KiB、待写载荷 8 MiB、单批 1 MiB；超限丢弃并记录健康计数。

载荷预算不包含托管对象、SQLite 页、索引、WAL 和备份的额外开销。

迁移前验证备份；所有跨版本步骤、表替换和 schema version 在同一事务提交，
成功后再启用 WAL。升级失败保留原 schema 与 journal mode。旧标量、事件和
raw 行保留，旧 raw 及备份可能含旧版明细；其隐私边界见 `privacy-v1.md`。
无单调时间的旧 raw 明确拒绝诊断回放，新投影按接收顺序流式读取，并验证
策略指纹。未来 schema 必须在只读预检阶段拒写。

投影帧按实例与实际 `deliverySequence` 去重，保留同一 Provider 观测被重复交付
时的质量变化；标量继续按组观测身份去重。预统计和实际流式读取都检查回放
帧数上限，拒绝把超限数据作为完整回放。
