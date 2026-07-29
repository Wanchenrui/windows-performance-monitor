# PerfMonitor Contract v1

`contractVersion` 固定为 `1.0`，与产品版本、实现语言和传输方式解耦。

## 公开响应

- `snapshot-v1.schema.json`：最新原子快照。
- `history-v1.schema.json`：有界历史查询和时间桶统计。
- `capabilities-v1.schema.json`：Provider、指标、限制和端点发现。
- `health-v1.schema.json`：轻量实例与健康探测。
- `error-v1.schema.json`：HTTP 错误。
- `legacy-stats-v0.2.schema.json`：deprecated `/api/stats` 适配器。

所有对象允许未知新增字段，旧客户端必须忽略未知字段。删除字段、修改单位、
复用 ID 或收窄既有枚举属于不兼容变更，必须发布新主契约版本。

指标、Provider 和错误码分别由 `metric-catalog.json`、
`provider-catalog.json` 与 `error-codes.json` 管理。展示文本不进入核心
契约；客户端依据机器 ID 本地化。

## 质量语义

每个指标组独立携带：

- `availability`：是否能得到数据及失败类别；
- `freshness`：`warming_up | fresh | stale`；
- `coverage`：`complete | limited` 及枚举/可读/跳过统计；
- `observedAtUtc`：该 Provider 的观测时间；
- `errors[].errorCode`：跨语言稳定错误码。

`null` 表示该指标在本次观测中没有值；字段缺失只允许用于未来扩展的未知
字段，不得用来替代已定义字段的不可用状态。

## Golden fixtures

`fixtures/` 由 Python 0.2 参考语义生成并冻结。运行：

```powershell
.\.venv\Scripts\python.exe .\scripts\generate_contract_fixtures.py
```

测试会比较重新生成结果与已提交文件，并使用 JSON Schema 和 .NET DTO
双重验证。
