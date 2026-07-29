import threading

from perf_monitor.store import MetricStore


def _snapshot(epoch_ms, marker, status="ok"):
    return {
        "collectedAt": f"sample-{marker}",
        "collectedAtEpochMs": epoch_ms,
        "overview": {
            "cpu": {"percent": marker},
            "memory": {"percent": marker},
        },
        "processes": [{"pid": marker}],
        "processCollection": {},
        "sample": {
            "intervalSeconds": 1,
            "durationMs": 1,
            "jitterMs": 0,
            "missedIntervalsTotal": 0,
            "skippedIntervalsAfterSample": 0,
        },
        "health": {
            "status": status,
            "stale": False,
            "ageSeconds": 0,
            "errors": [],
        },
    }


def test_history_is_trimmed_by_timestamp_not_point_count():
    store = MetricStore(
        history_window_seconds=10,
        sample_interval_seconds=1,
    )
    store.publish(_snapshot(100_000, 1))
    store.publish(_snapshot(105_000, 2))
    store.publish(_snapshot(111_000, 3))

    payload = store.get_payload(now_epoch_seconds=112)
    assert [item["sequence"] for item in payload["history"]] == [2, 3]
    assert payload["historyWindowSeconds"] == 10


def test_payload_copy_cannot_mutate_shared_snapshot():
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
    )
    store.publish(_snapshot(100_000, 7))

    first = store.get_payload(now_epoch_seconds=101)
    first["overview"]["cpu"]["percent"] = 99
    second = store.get_payload(now_epoch_seconds=101)

    assert second["overview"]["cpu"]["percent"] == 7


def test_old_successful_snapshot_is_marked_stale():
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
    )
    store.publish(_snapshot(100_000, 1))

    payload = store.get_payload(now_epoch_seconds=105)
    assert payload["health"]["stale"] is True
    assert payload["health"]["status"] == "stale"
    assert payload["health"]["ageSeconds"] == 5


def test_concurrent_reads_never_mix_snapshot_fields():
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
    )
    done = threading.Event()
    failures = []

    def writer():
        for marker in range(1, 301):
            store.publish(_snapshot(100_000 + marker, marker))
        done.set()

    def reader():
        while not done.is_set():
            payload = store.get_payload(now_epoch_seconds=100)
            if payload["sequence"] == 0:
                continue
            cpu_marker = payload["overview"]["cpu"]["percent"]
            process_marker = payload["processes"][0]["pid"]
            if cpu_marker != process_marker:
                failures.append((cpu_marker, process_marker))
                done.set()

    reader_thread = threading.Thread(target=reader)
    writer_thread = threading.Thread(target=writer)
    reader_thread.start()
    writer_thread.start()
    writer_thread.join(timeout=2)
    reader_thread.join(timeout=2)

    assert failures == []

