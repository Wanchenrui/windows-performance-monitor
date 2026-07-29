"""基于单调时钟的固定周期采样调度器。"""

from __future__ import annotations

import logging
import math
import threading
import time
from datetime import datetime, timedelta, timezone
from typing import Any, Callable

from .errors import stable_error_code


def calculate_next_tick(
    epoch: float,
    current_tick: int,
    interval: float,
    now: float,
) -> tuple[int, int]:
    """计算下一个绝对周期序号以及需要跳过的已过期周期数。"""

    if interval <= 0:
        raise ValueError("interval 必须大于 0")
    candidate = current_tick + 1
    candidate_deadline = epoch + candidate * interval
    if candidate_deadline >= now - 1e-9:
        return candidate, 0

    next_tick = math.floor((now - epoch) / interval) + 1
    next_tick = max(candidate, next_tick)
    return next_tick, max(0, next_tick - candidate)


class FixedPeriodSampler:
    """按 ``t_k = t_0 + kT`` 调度；超时跳过旧周期而不累计漂移。"""

    def __init__(
        self,
        collector: Any,
        store: Any,
        interval_seconds: float,
        *,
        clock: Callable[[], float] = time.monotonic,
        wall_clock: Callable[[], datetime] | None = None,
        logger: logging.Logger | None = None,
    ) -> None:
        if interval_seconds <= 0:
            raise ValueError("interval_seconds 必须大于 0")
        self.collector = collector
        self.store = store
        self.interval_seconds = float(interval_seconds)
        self.clock = clock
        self.wall_clock = wall_clock or (lambda: datetime.now(timezone.utc))
        self.logger = logger or logging.getLogger(__name__)
        self._stop_event = threading.Event()
        self._thread: threading.Thread | None = None

    @staticmethod
    def _as_utc(value: datetime) -> datetime:
        if value.tzinfo is None:
            value = value.astimezone()
        return value.astimezone(timezone.utc)

    def start(self) -> None:
        if self._thread and self._thread.is_alive():
            return
        self._thread = threading.Thread(
            target=self._run,
            name="metric-sampler",
            daemon=True,
        )
        self._thread.start()

    def stop(self, timeout: float = 5.0) -> None:
        self._stop_event.set()
        if self._thread and self._thread.is_alive():
            self._thread.join(timeout=timeout)

    def _run(self) -> None:
        epoch = self.clock()
        epoch_utc = self._as_utc(self.wall_clock())
        tick = 1  # 预留一个完整周期给非阻塞 CPU 计数器建立基线。
        missed_total = 0

        while not self._stop_event.is_set():
            deadline = epoch + tick * self.interval_seconds
            wait_seconds = max(0.0, deadline - self.clock())
            if self._stop_event.wait(wait_seconds):
                break

            started = self.clock()
            jitter_ms = max(0.0, (started - deadline) * 1000.0)
            started_at_utc = self._as_utc(self.wall_clock())
            scheduled_at_utc = epoch_utc + timedelta(
                seconds=tick * self.interval_seconds
            )

            try:
                result = self.collector.collect(started)
            except Exception as exc:  # 最外层防线：异常必须进入健康状态与日志
                self.logger.exception("采集批次失败")
                result = {
                    "status": "error",
                    "errors": [
                        {
                            "metric": "collector",
                            "code": stable_error_code(exc),
                            "nativeCode": exc.__class__.__name__,
                            "message": (str(exc).strip() or exc.__class__.__name__)[
                                :300
                            ],
                        }
                    ],
                    "overview": {},
                    "processes": [],
                    "processCollection": {},
                }

            finished = self.clock()
            completed_at_utc = self._as_utc(self.wall_clock())
            next_tick, skipped = calculate_next_tick(
                epoch,
                tick,
                self.interval_seconds,
                finished,
            )
            missed_total += skipped
            duration_ms = max(0.0, (finished - started) * 1000.0)

            snapshot = {
                "collectedAt": completed_at_utc.isoformat(
                    timespec="milliseconds"
                ),
                "collectedAtEpochMs": int(
                    completed_at_utc.timestamp() * 1000
                ),
                "scheduledAtUtc": scheduled_at_utc.isoformat(
                    timespec="milliseconds"
                ),
                "startedAtUtc": started_at_utc.isoformat(
                    timespec="milliseconds"
                ),
                "completedAtUtc": completed_at_utc.isoformat(
                    timespec="milliseconds"
                ),
                "scheduledAtMonotonic": deadline,
                "startedAtMonotonic": started,
                "completedAtMonotonic": finished,
                "providerObservedAtUtc": (
                    result.get("providerObservedAtUtc") or {}
                ),
                "overview": result.get("overview") or {},
                "processes": result.get("processes") or [],
                "processCollection": result.get("processCollection") or {},
                "sample": {
                    "intervalSeconds": self.interval_seconds,
                    "durationMs": round(duration_ms, 2),
                    "jitterMs": round(jitter_ms, 2),
                    "missedIntervalsTotal": missed_total,
                    "skippedIntervalsAfterSample": skipped,
                    "scheduledTick": tick,
                },
                "health": {
                    "status": result.get("status", "error"),
                    "stale": False,
                    "ageSeconds": 0.0,
                    "errors": result.get("errors") or [],
                },
            }
            self.store.publish(snapshot)

            if skipped:
                self.logger.warning(
                    "采样耗时 %.2f ms，跳过 %d 个过期周期",
                    duration_ms,
                    skipped,
                )
            tick = next_tick
