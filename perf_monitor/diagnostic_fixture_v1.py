"""Frozen contract-v1 diagnostic fixture builder for schema parity tests."""

from __future__ import annotations

from typing import Any


def build_diagnostics_fixture() -> dict[str, Any]:
    return {
        "contractVersion": "1.0",
        "productVersion": "0.6.0",
        "instanceId": "11111111111111111111111111111111",
        "query": {
            "ruleIds": ["system.high_cpu"],
            "states": ["active"],
            "fromEpochMs": 1_767_225_540_000,
            "toEpochMs": 1_767_225_660_000,
            "maxEvents": 200,
        },
        "eventCount": 1,
        "truncated": False,
        "events": [
            {
                "contractVersion": "1.0",
                "productVersion": "0.6.0",
                "eventId": (
                    "00000000000000000000000000000000"
                    "00000000000000000000000000000001"
                ),
                "instanceId": "11111111111111111111111111111111",
                "ruleId": "system.high_cpu",
                "ruleVersion": "1.0.0",
                "severity": "warning",
                "state": "active",
                "subjectId": "system",
                "hysteresis": {
                    "activateWhen": (
                        "system.cpu.utilization.percent >= 90"
                    ),
                    "recoverWhen": (
                        "system.cpu.utilization.percent <= 75"
                    ),
                },
                "debounce": {
                    "activateSeconds": 30,
                    "recoverSeconds": 30,
                },
                "cooldownSeconds": 300,
                "evidenceWindow": {
                    "fromUtc": "2026-01-01T00:00:00+00:00",
                    "toUtc": "2026-01-01T00:00:30+00:00",
                    "sampleCount": 2,
                },
                "firstSeenUtc": "2026-01-01T00:00:00+00:00",
                "lastSeenUtc": "2026-01-01T00:00:30+00:00",
                "confidence": 1,
                "evidence": [
                    {
                        "observedAtUtc": (
                            "2026-01-01T00:00:00+00:00"
                        ),
                        "subjectId": "system",
                        "signal": (
                            "system.cpu.utilization.percent"
                        ),
                        "value": 95,
                        "unit": "percent",
                        "condition": "breach",
                    },
                    {
                        "observedAtUtc": (
                            "2026-01-01T00:00:30+00:00"
                        ),
                        "subjectId": "system",
                        "signal": (
                            "system.cpu.utilization.percent"
                        ),
                        "value": 96,
                        "unit": "percent",
                        "condition": "breach",
                    },
                ],
            }
        ],
    }
