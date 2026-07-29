import json
import os
import sys

import pytest

from app import (
    SingleInstanceGuard,
    read_instance_url,
    remove_own_instance_state,
    write_instance_state,
)


def test_instance_state_round_trip_and_owned_cleanup(tmp_path):
    state_path = tmp_path / "PerfMonitor" / "instance.json"

    write_instance_state(state_path, 17700)
    payload = json.loads(state_path.read_text(encoding="utf-8"))

    assert payload["pid"] == os.getpid()
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
