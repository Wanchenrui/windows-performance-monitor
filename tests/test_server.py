import json
import threading
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import urlopen

from perf_monitor.server import create_server
from perf_monitor.store import MetricStore


def _snapshot(epoch_ms, marker):
    return {
        "collectedAt": f"sample-{marker}",
        "collectedAtEpochMs": epoch_ms,
        "overview": {
            "cpu": {"percent": marker},
            "memory": {"percent": marker},
        },
        "processes": [],
        "processCollection": {},
        "sample": {"intervalSeconds": 1},
        "health": {"status": "ok", "errors": []},
    }


def test_health_endpoint_identifies_service_and_sets_security_headers():
    static_dir = Path(__file__).resolve().parents[1] / "static"
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
    )
    server = create_server("127.0.0.1", 0, store, static_dir)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        port = server.server_address[1]
        with urlopen(f"http://127.0.0.1:{port}/api/health") as response:
            payload = json.loads(response.read().decode("utf-8"))
            assert response.headers["X-Content-Type-Options"] == "nosniff"
            assert response.headers["X-Frame-Options"] == "DENY"
        assert payload["service"] == "perf-monitor"
        assert payload["instanceId"] == store.instance_id
        assert payload["sequence"] == 0
        assert payload["contractVersion"] == "1.0"
        assert payload["summary"] == {
            "availability": "unavailable",
            "freshness": "warming_up",
        }
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def test_versioned_snapshot_history_and_capabilities_are_bounded():
    static_dir = Path(__file__).resolve().parents[1] / "static"
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
        instance_id="33333333333333333333333333333333",
    )
    for marker in range(1, 7):
        store.publish(_snapshot(marker * 1_000, marker))
    server = create_server("127.0.0.1", 0, store, static_dir)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        base_url = f"http://127.0.0.1:{server.server_address[1]}"
        with urlopen(f"{base_url}/api/v1/snapshot") as response:
            snapshot = json.loads(response.read().decode("utf-8"))
        assert snapshot["instanceId"] == "33333333333333333333333333333333"
        assert snapshot["contractVersion"] == "1.0"
        assert "history" not in snapshot

        history_url = (
            f"{base_url}/api/v1/history"
            "?metrics=system.cpu.utilization.percent&from=1000&to=6000&maxPoints=2"
        )
        with urlopen(history_url) as response:
            history = json.loads(response.read().decode("utf-8"))
        assert history["sourcePointCount"] == 6
        assert history["pointCount"] <= 2
        assert history["query"]["metricIds"] == [
            "system.cpu.utilization.percent"
        ]
        cpu_stats = history["points"][0]["metrics"][
            "system.cpu.utilization.percent"
        ]
        assert set(cpu_stats) == {"unit", "min", "max", "avg", "last"}

        with urlopen(f"{base_url}/api/v1/capabilities") as response:
            capabilities = json.loads(response.read().decode("utf-8"))
        assert capabilities["history"]["maxPoints"] == 5_000
        assert capabilities["history"]["ramPointLimit"] == 86_400

        with urlopen(f"{base_url}/api/stats") as response:
            legacy = json.loads(response.read().decode("utf-8"))
            assert response.headers["Deprecation"] == "true"
        assert legacy["deprecated"] is True
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def test_history_endpoint_rejects_unbounded_or_invalid_queries():
    static_dir = Path(__file__).resolve().parents[1] / "static"
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
    )
    server = create_server("127.0.0.1", 0, store, static_dir)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        base_url = f"http://127.0.0.1:{server.server_address[1]}"
        for query in (
            "maxPoints=5001",
            "maxPoints=not-a-number",
            "metrics=system.gpu.utilization.percent",
            "from=2000&to=1000",
        ):
            try:
                urlopen(f"{base_url}/api/v1/history?{query}")
            except HTTPError as exc:
                payload = json.loads(exc.read().decode("utf-8"))
                assert exc.code == 400
                assert payload["error"] == "invalid_query"
            else:
                raise AssertionError(f"查询应失败：{query}")
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)


def test_dashboard_polls_snapshot_and_loads_history_separately():
    script_path = Path(__file__).resolve().parents[1] / "static" / "dashboard.js"
    script = script_path.read_text(encoding="utf-8")

    assert 'fetch("/api/v1/snapshot"' in script
    assert "/api/v1/history?" in script
    assert 'fetch("/api/stats"' not in script
