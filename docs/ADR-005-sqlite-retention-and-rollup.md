# ADR-005：SQLite、保留与聚合策略

- 状态：已采用
- 日期：2026-07-29
- 目标实现：0.5.0

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
