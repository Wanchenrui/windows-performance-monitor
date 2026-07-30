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
- Top-N 进程仅短期保存，关注进程按需延长；
- 进程生命周期事件单独保存；
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
