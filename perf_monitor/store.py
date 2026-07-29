"""线程安全的实时快照与有界 RAM 历史窗口。"""

from __future__ import annotations

import copy
import math
import threading
import time
import uuid
from collections import deque
from typing import Any, Iterable

from . import API_VERSION, APP_VERSION


DEFAULT_HISTORY_POINT_LIMIT = 86_400
DEFAULT_HISTORY_QUERY_MAX_POINTS = 2_000
MAX_HISTORY_QUERY_POINTS = 5_000
SUPPORTED_HISTORY_METRICS = ("cpu", "mem")


class MetricStore:
    """在采样线程与 HTTP 请求线程之间提供原子快照和有界历史。"""

    def __init__(
        self,
        history_window_seconds: float,
        sample_interval_seconds: float,
        *,
        history_point_limit: int = DEFAULT_HISTORY_POINT_LIMIT,
        instance_id: str | None = None,
    ) -> None:
        if history_point_limit <= 0:
            raise ValueError("history_point_limit 必须大于 0")
        self.history_window_seconds = float(history_window_seconds)
        self.sample_interval_seconds = float(sample_interval_seconds)
        self.history_point_limit = int(history_point_limit)
        self.instance_id = instance_id or uuid.uuid4().hex
        self._lock = threading.RLock()
        self._sequence = 0

        # maxlen 是实际容器容量上限，不只是查询层的逻辑限制。历史记录发布
        # 后不再修改，因此查询只需做一次浅引用快照，无需深拷贝每个字典。
        self._history: deque[dict[str, Any]] = deque(
            maxlen=self.history_point_limit
        )
        self._snapshot: dict[str, Any] = {
            "sequence": 0,
            "collectedAt": None,
            "collectedAtEpochMs": None,
            "overview": {},
            "processes": [],
            "processCollection": {},
            "sample": {
                "intervalSeconds": self.sample_interval_seconds,
                "durationMs": None,
                "jitterMs": None,
                "missedIntervalsTotal": 0,
                "skippedIntervalsAfterSample": 0,
            },
            "health": {
                "status": "starting",
                "availabilityStatus": "starting",
                "freshness": "warming_up",
                "stale": True,
                "ageSeconds": None,
                "errors": [],
            },
        }

    def publish(self, snapshot: dict[str, Any]) -> int:
        """原子发布完整批次，并按时间及绝对点数双重裁剪 RAM 历史。"""

        with self._lock:
            self._sequence += 1
            owned = copy.deepcopy(snapshot)
            owned["sequence"] = self._sequence
            self._snapshot = owned

            epoch_ms = owned.get("collectedAtEpochMs")
            if isinstance(epoch_ms, (int, float)):
                overview = owned.get("overview") or {}
                cpu = overview.get("cpu") or {}
                memory = overview.get("memory") or {}
                record = {
                    "sequence": self._sequence,
                    "epochMs": int(epoch_ms),
                    "t": owned.get("collectedAt"),
                    "cpu": cpu.get("percent"),
                    "mem": memory.get("percent"),
                    "status": (owned.get("health") or {}).get(
                        "status", "error"
                    ),
                }
                self._history.append(record)
                self._trim_history_locked(record["epochMs"])
            return self._sequence

    def _trim_history_locked(self, latest_epoch_ms: int) -> None:
        cutoff_ms = int(
            latest_epoch_ms - self.history_window_seconds * 1000.0
        )
        while self._history and self._history[0]["epochMs"] < cutoff_ms:
            self._history.popleft()

    def get_snapshot(
        self,
        now_epoch_seconds: float | None = None,
    ) -> dict[str, Any]:
        """获取实时快照；该路径永远不复制或返回历史列表。"""

        now_epoch_seconds = (
            time.time() if now_epoch_seconds is None else now_epoch_seconds
        )
        with self._lock:
            snapshot = copy.deepcopy(self._snapshot)

        health = snapshot.setdefault("health", {})
        availability = health.get(
            "availabilityStatus",
            health.get("status", "error"),
        )
        health["availabilityStatus"] = availability
        collected_ms = snapshot.get("collectedAtEpochMs")
        if isinstance(collected_ms, (int, float)):
            age_seconds = max(0.0, now_epoch_seconds - collected_ms / 1000.0)
            stale_after = max(
                self.sample_interval_seconds * 3.0,
                self.sample_interval_seconds + 2.0,
            )
            stale = age_seconds > stale_after
            health["ageSeconds"] = round(age_seconds, 2)
            health["stale"] = stale
            health["freshness"] = "stale" if stale else "fresh"
            # 兼容旧客户端的单一 status，同时用 availabilityStatus 保留
            # partial/error，避免 stale 覆盖后丢失原始采集结果。
            health["status"] = "stale" if stale else availability
        else:
            health["ageSeconds"] = None
            health["stale"] = True
            health["freshness"] = "warming_up"
            health["status"] = availability

        snapshot.update(
            {
                "apiVersion": API_VERSION,
                "appVersion": APP_VERSION,
                "instanceId": self.instance_id,
                "historyWindowSeconds": self.history_window_seconds,
                "historyPointLimit": self.history_point_limit,
            }
        )
        return snapshot

    def get_history(
        self,
        *,
        metrics: Iterable[str] = SUPPORTED_HISTORY_METRICS,
        from_epoch_ms: int | None = None,
        to_epoch_ms: int | None = None,
        max_points: int = DEFAULT_HISTORY_QUERY_MAX_POINTS,
    ) -> dict[str, Any]:
        """查询有界历史，并按时间桶保留 min/max/avg/last。"""

        requested_metrics = tuple(dict.fromkeys(metrics))
        if not requested_metrics:
            raise ValueError("metrics 至少包含一个指标")
        unsupported = set(requested_metrics) - set(SUPPORTED_HISTORY_METRICS)
        if unsupported:
            names = ", ".join(sorted(unsupported))
            raise ValueError(f"不支持的历史指标：{names}")
        if not 1 <= max_points <= MAX_HISTORY_QUERY_POINTS:
            raise ValueError(
                f"maxPoints 必须在 1～{MAX_HISTORY_QUERY_POINTS} 之间"
            )
        if (
            from_epoch_ms is not None
            and to_epoch_ms is not None
            and from_epoch_ms > to_epoch_ms
        ):
            raise ValueError("from 不能晚于 to")

        with self._lock:
            # deque 的浅复制仅复制引用，昂贵的筛选和聚合全部在锁外完成。
            history_snapshot = list(self._history)
        records = [
            record
            for record in history_snapshot
            if (
                (from_epoch_ms is None or record["epochMs"] >= from_epoch_ms)
                and (to_epoch_ms is None or record["epochMs"] <= to_epoch_ms)
            )
        ]

        points = self._downsample(
            records,
            requested_metrics,
            max_points,
            from_epoch_ms,
            to_epoch_ms,
        )
        return {
            "apiVersion": API_VERSION,
            "appVersion": APP_VERSION,
            "instanceId": self.instance_id,
            "historyWindowSeconds": self.history_window_seconds,
            "historyPointLimit": self.history_point_limit,
            "metrics": list(requested_metrics),
            "fromEpochMs": from_epoch_ms,
            "toEpochMs": to_epoch_ms,
            "sourcePointCount": len(records),
            "pointCount": len(points),
            "downsampled": len(points) < len(records),
            "points": points,
        }

    @staticmethod
    def _downsample(
        records: list[dict[str, Any]],
        metrics: tuple[str, ...],
        max_points: int,
        requested_from_ms: int | None,
        requested_to_ms: int | None,
    ) -> list[dict[str, Any]]:
        if not records:
            return []

        if len(records) <= max_points:
            return [
                MetricStore._summarize_bucket([record], metrics)
                for record in records
            ]

        observed_epochs = [record["epochMs"] for record in records]
        start_ms = (
            requested_from_ms
            if requested_from_ms is not None
            else min(observed_epochs)
        )
        end_ms = (
            requested_to_ms
            if requested_to_ms is not None
            else max(observed_epochs)
        )
        span_ms = max(1, end_ms - start_ms + 1)
        bucket_width_ms = max(1, math.ceil(span_ms / max_points))
        buckets: dict[int, list[dict[str, Any]]] = {}

        for record in records:
            bucket_index = min(
                max_points - 1,
                max(0, (record["epochMs"] - start_ms) // bucket_width_ms),
            )
            buckets.setdefault(bucket_index, []).append(record)

        return [
            MetricStore._summarize_bucket(buckets[index], metrics)
            for index in sorted(buckets)
        ]

    @staticmethod
    def _summarize_bucket(
        records: list[dict[str, Any]],
        metrics: tuple[str, ...],
    ) -> dict[str, Any]:
        first = records[0]
        last = records[-1]
        point: dict[str, Any] = {
            "startEpochMs": min(record["epochMs"] for record in records),
            "endEpochMs": max(record["epochMs"] for record in records),
            "epochMs": last["epochMs"],
            "sequence": last["sequence"],
            "t": last.get("t"),
            "status": last.get("status", "error"),
            "stats": {},
        }
        for metric in metrics:
            values = [
                record.get(metric)
                for record in records
                if isinstance(record.get(metric), (int, float))
            ]
            last_value = next(
                (
                    record.get(metric)
                    for record in reversed(records)
                    if isinstance(record.get(metric), (int, float))
                ),
                None,
            )
            point[metric] = last_value
            point["stats"][metric] = {
                "min": min(values) if values else None,
                "max": max(values) if values else None,
                "avg": (
                    round(sum(values) / len(values), 4)
                    if values
                    else None
                ),
                "last": last_value,
            }
        return point

    def get_capabilities(self) -> dict[str, Any]:
        return {
            "apiVersion": API_VERSION,
            "appVersion": APP_VERSION,
            "instanceId": self.instance_id,
            "endpoints": {
                "snapshot": "/api/v1/snapshot",
                "history": "/api/v1/history",
                "capabilities": "/api/v1/capabilities",
                "health": "/api/v1/health",
            },
            "history": {
                "metrics": list(SUPPORTED_HISTORY_METRICS),
                "defaultMaxPoints": DEFAULT_HISTORY_QUERY_MAX_POINTS,
                "maxPoints": MAX_HISTORY_QUERY_POINTS,
                "ramPointLimit": self.history_point_limit,
                "aggregation": ["min", "max", "avg", "last"],
            },
        }

    def get_payload(
        self,
        now_epoch_seconds: float | None = None,
    ) -> dict[str, Any]:
        """0.2 兼容适配器；历史最多返回默认上限，禁止无界复制。"""

        snapshot = self.get_snapshot(now_epoch_seconds)
        history = self.get_history()
        snapshot.update(
            {
                "history": history["points"],
                "historyDownsampled": history["downsampled"],
                "historySourcePointCount": history["sourcePointCount"],
                "deprecated": True,
            }
        )
        return snapshot
