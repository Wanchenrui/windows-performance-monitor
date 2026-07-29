"""线程安全的实时快照与按真实时间裁剪的历史窗口。"""

from __future__ import annotations

import copy
import threading
import time
from collections import deque
from typing import Any

from . import API_VERSION, APP_VERSION


class MetricStore:
    """在采样线程与 HTTP 请求线程之间提供原子快照。"""

    def __init__(
        self,
        history_window_seconds: float,
        sample_interval_seconds: float,
    ) -> None:
        self.history_window_seconds = float(history_window_seconds)
        self.sample_interval_seconds = float(sample_interval_seconds)
        self._lock = threading.RLock()
        self._sequence = 0
        self._history: deque[dict[str, Any]] = deque()
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
                "stale": True,
                "ageSeconds": None,
                "errors": [],
            },
        }

    def publish(self, snapshot: dict[str, Any]) -> int:
        """原子发布一个完整批次，并依据时间戳裁剪历史数据。"""

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
                self._history.append(
                    {
                        "sequence": self._sequence,
                        "epochMs": int(epoch_ms),
                        "t": owned.get("collectedAt"),
                        "cpu": cpu.get("percent"),
                        "mem": memory.get("percent"),
                        "status": (owned.get("health") or {}).get(
                            "status", "error"
                        ),
                    }
                )
                cutoff_ms = int(
                    epoch_ms - self.history_window_seconds * 1000.0
                )
                while (
                    self._history
                    and self._history[0]["epochMs"] < cutoff_ms
                ):
                    self._history.popleft()
            return self._sequence

    def get_payload(self, now_epoch_seconds: float | None = None) -> dict[str, Any]:
        """获取带实时数据年龄的不可变副本。"""

        now_epoch_seconds = (
            time.time() if now_epoch_seconds is None else now_epoch_seconds
        )
        with self._lock:
            snapshot = copy.deepcopy(self._snapshot)
            history = copy.deepcopy(list(self._history))

        health = snapshot.setdefault("health", {})
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
            if stale and health.get("status") == "ok":
                health["status"] = "stale"
        else:
            health["ageSeconds"] = None
            health["stale"] = True

        snapshot.update(
            {
                "apiVersion": API_VERSION,
                "appVersion": APP_VERSION,
                "historyWindowSeconds": self.history_window_seconds,
                "history": history,
            }
        )
        return snapshot

