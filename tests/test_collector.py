import sys
import time
import os

import pytest

from perf_monitor.collector import (
    NativeWindowsCollector,
    calculate_process_cpu_percent,
)
from perf_monitor.windows_process import WindowsProcessProvider


def test_process_cpu_is_normalized_by_logical_processor_count():
    normalized, core_equivalent = calculate_process_cpu_percent(
        delta_cpu_seconds=2.36,
        elapsed_seconds=1.0,
        logical_cpu_count=16,
    )

    assert normalized == pytest.approx(14.75)
    assert core_equivalent == pytest.approx(236.0)


@pytest.mark.parametrize(
    ("elapsed", "logical_count"),
    [(0.0, 8), (-1.0, 8), (1.0, 0), (1.0, -1)],
)
def test_process_cpu_rejects_invalid_denominator(elapsed, logical_count):
    with pytest.raises(ValueError):
        calculate_process_cpu_percent(1.0, elapsed, logical_count)


@pytest.mark.skipif(sys.platform != "win32", reason="产品仅支持 Windows")
def test_native_collector_returns_explicit_units_and_sources():
    collector = NativeWindowsCollector(process_limit=5)
    time.sleep(0.05)
    first = collector.collect()
    time.sleep(0.05)
    result = collector.collect()

    assert result["status"] in {"ok", "partial"}
    assert result["overview"]["cpu"]["logicalCpuCount"] >= 1
    assert 0 <= result["overview"]["cpu"]["percent"] <= 100
    assert result["overview"]["cpu"]["source"]

    memory = result["overview"]["memory"]
    assert memory["totalBytes"] > 0
    assert 0 <= memory["usedBytes"] <= memory["totalBytes"]
    assert memory["source"]

    disk = result["overview"]["systemDisk"]
    assert disk["isSystem"] is True
    assert disk["totalBytes"] > 0
    assert disk["source"]

    assert all(process["pid"] != 0 for process in first["processes"])
    assert result["processCollection"]["status"] in {"complete", "limited"}
    for process in result["processes"]:
        assert "workingSetBytes" in process
        assert "privateBytes" in process
        if process["cpuReady"]:
            assert 0 <= process["cpuNormalizedPct"] <= 100
            assert process["cpuCoreEquivalentPct"] >= 0


@pytest.mark.skipif(sys.platform != "win32", reason="产品仅支持 Windows")
def test_windows_process_provider_reads_current_process_without_elevation():
    counters = WindowsProcessProvider().query(os.getpid())

    assert counters is not None
    assert counters.create_time_ticks > 0
    assert counters.cpu_total_seconds >= 0
    assert counters.working_set_bytes is not None
    assert counters.working_set_bytes > 0
    assert counters.private_bytes is not None
    assert counters.private_bytes > 0
