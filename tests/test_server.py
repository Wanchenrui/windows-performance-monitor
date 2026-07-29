import json
import threading
from pathlib import Path
from urllib.request import urlopen

from perf_monitor.server import create_server
from perf_monitor.store import MetricStore


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
        assert payload["sequence"] == 0
        assert payload["health"]["status"] == "starting"
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)

