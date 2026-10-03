"""生成并冻结 Python 参考实现的契约 v1 golden fixtures。"""

from __future__ import annotations

import argparse
import copy
import json
import sys
from pathlib import Path
from typing import Any

PROJECT_ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(PROJECT_ROOT))

from perf_monitor.diagnostic_fixture_v1 import build_diagnostics_fixture

from perf_monitor.contract_v1 import (
    build_capabilities,
    build_health,
    build_history,
    build_snapshot,
)


INSTANCE_ID = "11111111111111111111111111111111"
BASE_TIME = "2026-07-29T08:00:00.000+00:00"


def _legacy_snapshot() -> dict[str, Any]:
    return {
        "apiVersion": "0.2.1",
        "appVersion": "0.3.0",
        "instanceId": INSTANCE_ID,
        "sequence": 42,
        "scheduledAtUtc": BASE_TIME,
        "startedAtUtc": "2026-07-29T08:00:00.002+00:00",
        "completedAtUtc": "2026-07-29T08:00:00.036+00:00",
        "collectedAt": "2026-07-29T08:00:00.036+00:00",
        "collectedAtEpochMs": 1_775_030_400_036,
        "providerObservedAtUtc": {
            "systemCpu": "2026-07-29T08:00:00.005+00:00",
            "memory": "2026-07-29T08:00:00.007+00:00",
            "volumes": "2026-07-29T08:00:00.012+00:00",
            "uptime": "2026-07-29T08:00:00.013+00:00",
            "processes": "2026-07-29T08:00:00.034+00:00",
        },
        "historyWindowSeconds": 3_600.0,
        "historyPointLimit": 86_400,
        "overview": {
            "cpu": {"percent": 23.5, "logicalCpuCount": 16},
            "memory": {
                "percent": 61.2,
                "usedBytes": 21_028_454_400,
                "availableBytes": 13_331_021_824,
                "totalBytes": 34_359_476_224,
            },
            "disks": [
                {
                    "device": "C:\\",
                    "mountpoint": "C:\\",
                    "fileSystem": "NTFS",
                    "percent": 54.0,
                    "usedBytes": 270_000_000_000,
                    "freeBytes": 230_000_000_000,
                    "totalBytes": 500_000_000_000,
                    "isSystem": True,
                }
            ],
            "systemDisk": {"device": "C:\\"},
            "uptimeSeconds": 123_456,
        },
        "processes": [
            {
                "pid": 4242,
                "creationTimeTicks": 133_000_000_000_000_000,
                "name": "example.exe",
                "cpuNormalizedPct": 12.5,
                "cpuCoreEquivalentPct": 200.0,
                "cpuReady": True,
                "workingSetBytes": 200_000_000,
                "privateBytes": 150_000_000,
            }
        ],
        "processCollection": {
            "enumerated": 220,
            "skipped": 0,
            "cpuReady": 218,
            "status": "complete",
        },
        "sample": {
            "intervalSeconds": 1.0,
            "durationMs": 34.0,
            "jitterMs": 2.0,
            "missedIntervalsTotal": 0,
            "skippedIntervalsAfterSample": 0,
        },
        "health": {
            "status": "ok",
            "availabilityStatus": "ok",
            "freshness": "fresh",
            "stale": False,
            "ageSeconds": 0.2,
            "errors": [],
        },
    }


def generate_fixtures() -> dict[str, dict[str, Any]]:
    normal_legacy = _legacy_snapshot()
    normal = build_snapshot(normal_legacy)

    partial_legacy = copy.deepcopy(normal_legacy)
    partial_legacy["overview"]["memory"] = None
    partial_legacy["health"].update(
        {
            "status": "partial",
            "availabilityStatus": "partial",
            "errors": [
                {
                    "metric": "memory",
                    "code": "provider_failure",
                    "nativeCode": "OSError",
                    "message": "fixture-only native detail",
                }
            ],
        }
    )
    partial = build_snapshot(partial_legacy)

    stale_legacy = copy.deepcopy(normal_legacy)
    stale_legacy["health"].update(
        {
            "status": "stale",
            "availabilityStatus": "ok",
            "freshness": "stale",
            "stale": True,
            "ageSeconds": 15.0,
        }
    )
    stale = build_snapshot(stale_legacy)

    denied_legacy = copy.deepcopy(normal_legacy)
    denied_legacy["processes"] = []
    denied_legacy["processCollection"].update(
        {"skipped": 220, "cpuReady": 0, "status": "limited"}
    )
    denied_legacy["health"].update(
        {
            "status": "partial",
            "availabilityStatus": "partial",
            "errors": [
                {
                    "metric": "processes",
                    "code": "access_denied",
                    "nativeCode": "AccessDenied",
                    "message": "fixture-only native detail",
                }
            ],
        }
    )
    permission_denied = build_snapshot(denied_legacy)

    churn_before_legacy = copy.deepcopy(normal_legacy)
    churn_after_legacy = copy.deepcopy(normal_legacy)
    churn_after_legacy["sequence"] = 43
    churn_after_legacy["completedAtUtc"] = "2026-07-29T08:00:01.030+00:00"
    churn_after_legacy["collectedAt"] = churn_after_legacy["completedAtUtc"]
    churn_after_legacy["processes"][0].update(
        {
            "creationTimeTicks": 133_000_000_100_000_000,
            "name": "replacement.exe",
            "cpuNormalizedPct": None,
            "cpuCoreEquivalentPct": None,
            "cpuReady": False,
        }
    )
    process_churn = {
        "contractVersion": "1.0",
        "scenarioId": "process_pid_reuse",
        "snapshots": [
            build_snapshot(churn_before_legacy),
            build_snapshot(churn_after_legacy),
        ],
    }

    legacy_history = {
        "instanceId": INSTANCE_ID,
        "fromEpochMs": 1_775_030_399_000,
        "toEpochMs": 1_775_030_400_000,
        "requestedMaxPoints": 2,
        "sourcePointCount": 2,
        "downsampled": False,
        "points": [
            {
                "startEpochMs": 1_775_030_399_000,
                "endEpochMs": 1_775_030_399_000,
                "sequence": 41,
                "stats": {
                    "cpu": {"min": 20.0, "max": 20.0, "avg": 20.0, "last": 20.0},
                    "mem": {"min": 60.0, "max": 60.0, "avg": 60.0, "last": 60.0},
                },
            },
            {
                "startEpochMs": 1_775_030_400_000,
                "endEpochMs": 1_775_030_400_000,
                "sequence": 42,
                "stats": {
                    "cpu": {"min": 23.5, "max": 23.5, "avg": 23.5, "last": 23.5},
                    "mem": {"min": 61.2, "max": 61.2, "avg": 61.2, "last": 61.2},
                },
            },
        ],
    }
    history = build_history(
        legacy_history,
        (
            "system.cpu.utilization.percent",
            "system.memory.utilization.percent",
        ),
    )
    legacy_capabilities = {
        "instanceId": INSTANCE_ID,
        "sampleIntervalSeconds": 1.0,
        "endpoints": {
            "snapshot": "/api/v1/snapshot",
            "history": "/api/v1/history",
            "capabilities": "/api/v1/capabilities",
            "health": "/api/v1/health",
        },
        "history": {
            "defaultMaxPoints": 2_000,
            "maxPoints": 5_000,
            "ramPointLimit": 86_400,
            "aggregation": ["min", "max", "avg", "last"],
        },
    }
    capabilities = build_capabilities(legacy_capabilities)

    return {
        "snapshot-normal.json": normal,
        "snapshot-partial.json": partial,
        "snapshot-stale.json": stale,
        "snapshot-permission-denied.json": permission_denied,
        "scenario-process-churn.json": process_churn,
        "history-normal.json": history,
        "capabilities-normal.json": capabilities,
        "health-normal.json": build_health(normal),
        "diagnostics-normal.json": build_diagnostics_fixture(),
    }


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument(
        "--output",
        type=Path,
        default=(
            PROJECT_ROOT / "contracts" / "v1" / "fixtures"
        ),
    )
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)

    for filename, payload in generate_fixtures().items():
        (args.output / filename).write_text(
            json.dumps(payload, ensure_ascii=False, indent=2) + "\n",
            encoding="utf-8",
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
