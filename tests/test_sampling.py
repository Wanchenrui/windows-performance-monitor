import time
from datetime import datetime, timezone

import pytest

from perf_monitor.sampling import FixedPeriodSampler, calculate_next_tick


def test_next_tick_stays_on_absolute_schedule():
    next_tick, skipped = calculate_next_tick(
        epoch=100.0,
        current_tick=1,
        interval=1.0,
        now=101.4,
    )
    assert (next_tick, skipped) == (2, 0)

    # 恰好到达下一期限时应立即执行该周期，而不是误判为缺口。
    next_tick, skipped = calculate_next_tick(
        epoch=100.0,
        current_tick=1,
        interval=1.0,
        now=102.0,
    )
    assert (next_tick, skipped) == (2, 0)


def test_next_tick_skips_expired_deadlines_without_drift():
    next_tick, skipped = calculate_next_tick(
        epoch=100.0,
        current_tick=1,
        interval=1.0,
        now=104.2,
    )
    assert next_tick == 5
    assert skipped == 3
    assert 100.0 + next_tick * 1.0 == pytest.approx(105.0)


class _SlowCollector:
    def __init__(self, delay_seconds):
        self.delay_seconds = delay_seconds

    def collect(self, sample_monotonic):
        time.sleep(self.delay_seconds)
        return {
            "status": "ok",
            "errors": [],
            "overview": {
                "cpu": {"percent": 1.0},
                "memory": {"percent": 2.0},
            },
            "processes": [],
            "processCollection": {},
        }


class _RecordingStore:
    def __init__(self):
        self.snapshots = []

    def publish(self, snapshot):
        self.snapshots.append(snapshot)


def test_sampler_marks_overrun_as_skipped_interval():
    store = _RecordingStore()
    sampler = FixedPeriodSampler(
        _SlowCollector(delay_seconds=0.075),
        store,
        interval_seconds=0.03,
        wall_clock=lambda: datetime.now(timezone.utc),
    )
    sampler.start()
    time.sleep(0.20)
    sampler.stop()

    assert store.snapshots
    assert any(
        snapshot["sample"]["skippedIntervalsAfterSample"] > 0
        for snapshot in store.snapshots
    )
    assert store.snapshots[-1]["sample"]["missedIntervalsTotal"] > 0

