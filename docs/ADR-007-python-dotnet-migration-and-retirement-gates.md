# ADR-007：Python → .NET 迁移与退役门禁

- 状态：已采用
- 日期：2026-07-29
- 目标实现：0.4.0～0.5.0

## 决策

Python 0.2/0.3 不再横向增加指标，而作为指标口径 oracle、fixture 生成器
和差分验证基准。`.NET 10` Agent 旁路运行，两端输出同一 contract v1。

差分比较按对齐时间窗的均值和分位数执行，不对瞬时 CPU 点做相等判断。
首批门限：

- 系统 CPU 均值绝对差不超过 3 个百分点；
- 内存占用绝对差不超过 1 个百分点；
- 容量、单位、空值和进程身份为精确语义比较。

## 必测场景

空闲、单核/多核压力、内存分配释放、500+ 进程、高频创建退出、受保护
进程、用户切换、睡眠恢复、系统时钟前后跳、Agent 高负载、Provider 超时
和异常。

## 退役门禁

Python 高频 Agent 只有在以下条件全部满足后退役：

1. 所有 golden fixtures 能被 .NET 反序列化且 Schema 通过；
2. 核心指标通过差分门限；
3. PID 复用不污染 CPU 基线；
4. 72 小时稳定性测试通过，无无界内存增长；
5. Provider 故障局部降级；
6. Desktop/Named Pipe/SQLite 路径完成 0.5 门禁；
7. 发生回归时可切回最后一个 Python 标签。

Python 源码和 traces 在退役后保留为只读参考，不再承担产品实时采集。

## 0.5 退役结论

默认启动、构建产物和 CI 发布物均已切换为 .NET Agent/Desktop；Python
不会被打包为产品高频 Agent。Python 路径只允许用于：

1. golden fixture 与 Schema 回归；
2. Python/.NET 同窗差分；
3. 显式 `--dev-http` 开发模式；
4. 回归时读取最后一个 Python 产品标签 `v0.3.0`。

真实 72 小时 Agent 门禁和 0.5 Desktop/Named Pipe/SQLite 门禁均是发布
前置条件；短时 CI 或虚拟 259,200 周期测试不能替代真实墙钟证据。
