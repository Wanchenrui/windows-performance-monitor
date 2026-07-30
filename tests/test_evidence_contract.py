import json
from pathlib import Path

from jsonschema import Draft202012Validator, FormatChecker

from scripts.validate_evidence import validate_evidence


ROOT = Path(__file__).resolve().parents[1]
CONTRACTS = ROOT / "contracts" / "v1"
TRACE = (
    ROOT
    / "reference"
    / "traces"
    / "v0.4-local-differential-1653399.json"
)


def test_evidence_schemas_are_valid_draft_2020_12():
    for path in sorted(CONTRACTS.glob("*.schema.json")):
        schema = json.loads(path.read_text(encoding="utf-8"))
        Draft202012Validator.check_schema(schema)


def test_committed_differential_evidence_validates():
    validate_evidence(
        "differential-evidence-v1.schema.json",
        TRACE,
    )


def test_release_soak_schema_rejects_accelerated_evidence_claim():
    schema = json.loads(
        (
            CONTRACTS / "soak-evidence-v1.schema.json"
        ).read_text(encoding="utf-8")
    )
    payload = {
        "contractVersion": "1.0",
        "productVersion": "0.4.0",
        "profile": "release-72h",
        "startedAtUtc": "2026-07-29T00:00:00Z",
        "completedAtUtc": "2026-07-29T00:01:00Z",
        "requestedDurationSeconds": 45,
        "actualElapsedSeconds": 48,
        "release72HourGate": {
            "requiredDurationSeconds": 259200,
            "actualWallClockPassed": False,
        },
        "snapshots": {
            "count": 2,
            "firstSequence": 1,
            "lastSequence": 2,
            "maximumAllowed": 12,
            "passed": True,
        },
        "resources": {
            "samples": 3,
            "workingSet": {"peakMiB": 1, "limitMiB": 384},
            "privateMemory": {
                "peakMiB": 1,
                "limitMiB": 384,
                "headMedianMiB": 1,
                "tailMedianMiB": 1,
                "retainedGrowthMiB": 0,
                "growthLimitMiB": 64,
                "regressionSlopeMiBPerHour": 0,
            },
            "gcHeap": {
                "firstMiB": 1,
                "lastMiB": 1,
                "growthMiB": 0,
                "growthLimitMiB": 32,
            },
            "cpuCoreEquivalentMeanPct": 1,
            "passed": True,
        },
        "passed": True,
    }
    errors = list(
        Draft202012Validator(
            schema,
            format_checker=FormatChecker(),
        ).iter_errors(payload)
    )
    assert errors
