import copy
import json
import threading
from pathlib import Path
from urllib.error import HTTPError
from urllib.request import urlopen

import pytest
from jsonschema import Draft202012Validator, FormatChecker
from referencing import Registry, Resource

from perf_monitor.contract_v1 import (
    METRIC_UNITS,
    PROVIDER_IDS,
    SOURCE_IDS,
)
from perf_monitor.errors import stable_error_code
from perf_monitor.server import create_server
from perf_monitor.store import MetricStore
from scripts.generate_contract_fixtures import generate_fixtures


ROOT = Path(__file__).resolve().parents[1]
CONTRACTS = ROOT / "contracts" / "v1"
FIXTURES = CONTRACTS / "fixtures"


def _load_json(path):
    return json.loads(path.read_text(encoding="utf-8"))


SCHEMAS = {
    path.name: _load_json(path)
    for path in CONTRACTS.glob("*.schema.json")
}
REGISTRY = Registry().with_resources(
    [
        (schema["$id"], Resource.from_contents(schema))
        for schema in SCHEMAS.values()
    ]
)


def _validate(schema_name, payload):
    validator = Draft202012Validator(
        SCHEMAS[schema_name],
        registry=REGISTRY,
        format_checker=FormatChecker(),
    )
    errors = sorted(validator.iter_errors(payload), key=lambda error: list(error.path))
    assert errors == [], "\n".join(
        f"{'/'.join(map(str, error.path))}: {error.message}"
        for error in errors
    )


@pytest.mark.parametrize(
    ("fixture_name", "schema_name"),
    [
        ("snapshot-normal.json", "snapshot-v1.schema.json"),
        ("snapshot-partial.json", "snapshot-v1.schema.json"),
        ("snapshot-stale.json", "snapshot-v1.schema.json"),
        ("snapshot-permission-denied.json", "snapshot-v1.schema.json"),
        ("scenario-process-churn.json", "scenario-v1.schema.json"),
        ("history-normal.json", "history-v1.schema.json"),
        ("capabilities-normal.json", "capabilities-v1.schema.json"),
        ("health-normal.json", "health-v1.schema.json"),
    ],
)
def test_golden_fixtures_validate_against_schema(fixture_name, schema_name):
    _validate(schema_name, _load_json(FIXTURES / fixture_name))


def test_golden_fixtures_are_reproducible_from_python_reference():
    generated = generate_fixtures()
    committed = {
        path.name: _load_json(path)
        for path in FIXTURES.glob("*.json")
    }

    assert committed == generated


def test_contract_allows_unknown_additive_fields():
    payload = _load_json(FIXTURES / "snapshot-normal.json")
    payload["futureEnvelopeField"] = {"enabled": True}
    payload["groups"]["systemCpu"]["futureGroupField"] = "ignored"
    payload["groups"]["systemCpu"]["data"]["metrics"][
        "system.cpu.utilization.percent"
    ]["futureMetricField"] = 1

    _validate("snapshot-v1.schema.json", payload)


def test_catalogs_match_python_contract_constants():
    metric_catalog = _load_json(CONTRACTS / "metric-catalog.json")
    provider_catalog = _load_json(CONTRACTS / "provider-catalog.json")
    catalog_metrics = {
        item["metricId"]: item["unit"]
        for item in metric_catalog["metrics"]
    }
    catalog_providers = {
        (item["groupId"], item["providerId"])
        for item in provider_catalog["providers"]
    }
    catalog_sources = {
        source_id
        for item in metric_catalog["metrics"]
        for source_id in item["sourceIds"]
    }

    assert set(METRIC_UNITS.items()).issubset(catalog_metrics.items())
    assert {
        (group_id, provider_id)
        for group_id, provider_id in PROVIDER_IDS.items()
    }.issubset(catalog_providers)
    assert set(SOURCE_IDS.values()).issubset(catalog_sources)


def test_contract_never_exposes_python_exception_class_names():
    partial = (FIXTURES / "snapshot-partial.json").read_text(encoding="utf-8")
    denied = (FIXTURES / "snapshot-permission-denied.json").read_text(
        encoding="utf-8"
    )

    assert "OSError" not in partial
    assert "AccessDenied" not in denied
    assert stable_error_code(PermissionError()) == "access_denied"
    assert stable_error_code(TimeoutError()) == "timeout"
    assert stable_error_code(NotImplementedError()) == "not_supported"


def test_process_churn_fixture_uses_pid_and_creation_time_identity():
    scenario = _load_json(FIXTURES / "scenario-process-churn.json")
    before, after = scenario["snapshots"]
    before_identity = before["groups"]["processes"]["data"][0]["identity"]
    after_identity = after["groups"]["processes"]["data"][0]["identity"]

    assert before_identity["pid"] == after_identity["pid"]
    assert before_identity["creationTimeTicks"] != after_identity[
        "creationTimeTicks"
    ]
    assert after["groups"]["processes"]["data"][0]["cpuReady"] is False


def _legacy_snapshot(epoch_ms=1_775_030_400_000):
    completed = "2026-07-29T08:00:00.000+00:00"
    return {
        "collectedAt": completed,
        "collectedAtEpochMs": epoch_ms,
        "scheduledAtUtc": completed,
        "startedAtUtc": completed,
        "completedAtUtc": completed,
        "providerObservedAtUtc": {
            name: completed
            for name in ("systemCpu", "memory", "volumes", "uptime", "processes")
        },
        "overview": {
            "cpu": {"percent": 10.0, "logicalCpuCount": 8},
            "memory": {
                "percent": 20.0,
                "usedBytes": 20,
                "availableBytes": 80,
                "totalBytes": 100,
            },
            "disks": [],
            "systemDisk": None,
            "uptimeSeconds": 1,
        },
        "processes": [],
        "processCollection": {
            "enumerated": 0,
            "skipped": 0,
            "status": "complete",
        },
        "sample": {
            "intervalSeconds": 1,
            "durationMs": 1,
            "jitterMs": 0,
            "missedIntervalsTotal": 0,
            "skippedIntervalsAfterSample": 0,
        },
        "health": {
            "status": "ok",
            "availabilityStatus": "ok",
            "freshness": "fresh",
            "stale": False,
            "ageSeconds": 0,
            "errors": [],
        },
    }


def test_every_public_json_response_has_a_schema():
    store = MetricStore(
        history_window_seconds=60,
        sample_interval_seconds=1,
        instance_id="22222222222222222222222222222222",
    )
    store.publish(_legacy_snapshot())
    server = create_server("127.0.0.1", 0, store, ROOT / "static")
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    try:
        base = f"http://127.0.0.1:{server.server_address[1]}"
        cases = [
            ("/api/v1/snapshot", "snapshot-v1.schema.json"),
            (
                "/api/v1/history"
                "?metrics=system.cpu.utilization.percent&maxPoints=10",
                "history-v1.schema.json",
            ),
            (
                "/api/v1/history?metrics="
                "system.cpu.utilization.percent,"
                "system.cpu.utilization.percent&maxPoints=10",
                "history-v1.schema.json",
            ),
            ("/api/v1/capabilities", "capabilities-v1.schema.json"),
            ("/api/v1/health", "health-v1.schema.json"),
            ("/api/health", "health-v1.schema.json"),
            ("/api/stats", "legacy-stats-v0.2.schema.json"),
        ]
        for route, schema_name in cases:
            with urlopen(base + route) as response:
                _validate(
                    schema_name,
                    json.loads(response.read().decode("utf-8")),
                )

        with pytest.raises(HTTPError) as error:
            urlopen(base + "/api/v1/history?metrics=unknown")
        _validate(
            "error-v1.schema.json",
            json.loads(error.value.read().decode("utf-8")),
        )
    finally:
        server.shutdown()
        server.server_close()
        thread.join(timeout=2)
