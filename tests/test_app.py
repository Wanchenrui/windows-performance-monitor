import json
import logging
import os
import sys
from types import SimpleNamespace

import pytest

from app import (
    SingleInstanceGuard,
    probe_existing_instance,
    read_instance_url,
    remove_own_instance_state,
    run_primary_instance,
    write_instance_state,
)


def test_instance_state_round_trip_and_owned_cleanup(tmp_path):
    state_path = tmp_path / "PerfMonitor" / "instance.json"

    instance_id = "0123456789abcdef0123456789abcdef"
    write_instance_state(state_path, 17700, instance_id)
    payload = json.loads(state_path.read_text(encoding="utf-8"))

    assert payload["pid"] == os.getpid()
    assert payload["instanceId"] == instance_id
    assert read_instance_url(state_path) == "http://127.0.0.1:17700"

    remove_own_instance_state(state_path)
    assert not state_path.exists()


def test_invalid_instance_state_is_not_trusted(tmp_path):
    state_path = tmp_path / "instance.json"
    state_path.write_text(
        '{"service":"different-product","port":7700}',
        encoding="utf-8",
    )

    assert read_instance_url(state_path) is None


def test_instance_state_without_random_identity_is_not_trusted(tmp_path):
    state_path = tmp_path / "instance.json"
    state_path.write_text(
        '{"service":"perf-monitor","port":7700}',
        encoding="utf-8",
    )

    assert read_instance_url(state_path) is None


def test_service_string_alone_cannot_spoof_existing_instance(monkeypatch):
    class _Response:
        def __enter__(self):
            return self

        def __exit__(self, exc_type, exc, traceback):
            return False

        @staticmethod
        def read():
            return json.dumps(
                {
                    "service": "perf-monitor",
                    "instanceId": "attacker-instance",
                }
            ).encode("utf-8")

    monkeypatch.setattr("app.urlopen", lambda *args, **kwargs: _Response())

    assert probe_existing_instance(
        "http://127.0.0.1:7700",
        "expected-instance",
    ) is False


def test_port_conflict_never_falls_back_to_service_string_probe(
    monkeypatch,
    tmp_path,
):
    def raise_port_conflict(*args, **kwargs):
        raise OSError("occupied")

    def fail_if_probed(*args, **kwargs):
        raise AssertionError("端口冲突时不得探测 service 字符串")

    monkeypatch.setattr("app.create_server", raise_port_conflict)
    monkeypatch.setattr("app.probe_existing_instance", fail_if_probed)
    monkeypatch.setattr("app.show_error", lambda *args: None)
    monkeypatch.setattr(
        "app.instance_state_path",
        lambda: tmp_path / "instance.json",
    )
    args = SimpleNamespace(
        history_minutes=60,
        sample_interval=1,
        port=17700,
        no_browser=True,
        no_tray=True,
        exit_after_seconds=None,
    )

    result = run_primary_instance(
        args,
        logging.getLogger("test"),
        None,
        "http://127.0.0.1:17700",
        "0123456789abcdef0123456789abcdef",
    )

    assert result == 2


@pytest.mark.skipif(sys.platform != "win32", reason="产品仅支持 Windows")
def test_named_mutex_allows_only_one_instance():
    mutex_name = f"Local\\PerfMonitor-Test-{os.getpid()}"
    first = SingleInstanceGuard(mutex_name)
    second = SingleInstanceGuard(mutex_name)
    try:
        assert first.acquired is True
        assert second.acquired is False
    finally:
        second.close()
        first.close()
