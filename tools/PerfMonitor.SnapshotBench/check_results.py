"""Recompute every snapshot microbenchmark case from its unfiltered raw CSV.

This verifier uses only Python's standard library. It does not run the product.
Call with one or more before/after sample directories, optionally --output PATH.
"""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import statistics
from collections import defaultdict
from pathlib import Path


OPERATIONS = {
    "timer-control", "read-envelope", "read-payload-once", "read-payload-three",
    "serialize-existing", "read-serialize", "watched-projection",
    "publish-small-held", "publish-process",
}


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def close(actual: float, expected: float) -> None:
    if not math.isclose(actual, expected, rel_tol=1e-10, abs_tol=1e-6):
        raise AssertionError(f"numeric mismatch: {actual!r} != {expected!r}")


def nearest_rank(values: list[int], percentile: float) -> int:
    return sorted(values)[math.ceil(len(values) * percentile) - 1]


def check(directory: Path) -> dict:
    summary = json.loads((directory / "summary.json").read_text(encoding="utf-8-sig"))
    raw_path = directory / "samples.csv"
    if sha256(raw_path) != summary["samplesSha256"]:
        raise AssertionError(f"raw CSV hash mismatch: {directory}")
    grouped: dict[tuple[int, int, str], list[dict]] = defaultdict(list)
    with raw_path.open(encoding="utf-8-sig", newline="") as source:
        for row in csv.DictReader(source):
            key = (int(row["trial"]), int(row["rows"]), row["operation"])
            ticks = int(row["elapsedTicks"])
            allocation = int(row["allocatedBytes"])
            if ticks < 0 or allocation < 0:
                raise AssertionError("negative duration or allocation")
            close(float(row["elapsedMicroseconds"]), ticks * 1e6 / summary["stopwatchFrequency"])
            grouped[key].append(row)
    expected = {
        (trial, rows, operation)
        for trial in range(1, summary["trials"] + 1)
        for rows in (100, 500, 1000)
        for operation in OPERATIONS
    }
    if set(grouped) != expected or len(summary["cases"]) != len(expected):
        raise AssertionError("missing or unexpected scenario")
    case_keys = set()
    for case in summary["cases"]:
        key = (case["trial"], case["rows"], case["operation"])
        if key in case_keys:
            raise AssertionError("duplicate summary case")
        case_keys.add(key)
        raw = grouped[key]
        if len(raw) != summary["iterations"] or case["samples"] != len(raw):
            raise AssertionError("sample count mismatch")
        if sorted(int(row["sample"]) for row in raw) != list(range(1, len(raw) + 1)):
            raise AssertionError("sample index mismatch")
        ticks = [int(row["elapsedTicks"]) for row in raw]
        allocations = [int(row["allocatedBytes"]) for row in raw]
        close(case["totalAllocatedBytes"], sum(allocations))
        close(case["meanAllocatedBytes"], statistics.mean(allocations))
        close(case["meanMicroseconds"], statistics.mean(ticks) * 1e6 / summary["stopwatchFrequency"])
        close(case["minMicroseconds"], min(ticks) * 1e6 / summary["stopwatchFrequency"])
        close(case["maxMicroseconds"], max(ticks) * 1e6 / summary["stopwatchFrequency"])
        for name, percentile in (("p50Microseconds", .5), ("p95Microseconds", .95), ("p99Microseconds", .99)):
            close(case[name], nearest_rank(ticks, percentile) * 1e6 / summary["stopwatchFrequency"])
    medians = []
    for rows in (100, 500, 1000):
        for operation in sorted(OPERATIONS):
            cases = [case for case in summary["cases"] if case["rows"] == rows and case["operation"] == operation]
            medians.append({
                "rows": rows, "operation": operation,
                **{name: statistics.median(case[name] for case in cases)
                   for name in ("meanAllocatedBytes", "p95Microseconds", "p99Microseconds")},
            })
    return {
        "directory": str(directory.resolve()), "label": summary["label"],
        "sourceHead": summary["sourceHead"], "summarySha256": sha256(directory / "summary.json"),
        "samplesSha256": summary["samplesSha256"], "casesVerified": len(grouped),
        "samplesVerified": sum(map(len, grouped.values())), "inputs": summary["inputs"],
        "sourceHashes": summary["sourceHashes"], "trialMedians": medians,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("directories", type=Path, nargs="+")
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    results = [check(directory) for directory in args.directories]
    if any(result["inputs"] != results[0]["inputs"] for result in results[1:]):
        raise AssertionError("before/after synthetic provider or initial snapshot bytes differ")
    if any(result["sourceHead"] != results[0]["sourceHead"] for result in results[1:]):
        raise AssertionError("before/after HEAD differs")
    harness = "tools/PerfMonitor.SnapshotBench/Program.cs"
    if any(result["sourceHashes"][harness] != results[0]["sourceHashes"][harness] for result in results[1:]):
        raise AssertionError("before/after C# measurement harness differs")
    output = {
        "valid": True, "method": "every raw sample independently recalculated; trial median is median of per-trial statistics, not pooled P95/P99",
        "verifierSha256": sha256(Path(__file__)), "results": results,
    }
    text = json.dumps(output, ensure_ascii=False, indent=2) + "\n"
    if args.output:
        if args.output.exists():
            raise FileExistsError("Preserve prior verification output")
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(text, encoding="utf-8")
    print(f"Verified {len(results)} runs, {sum(result['samplesVerified'] for result in results)} raw samples; inputs and measurement harness match.")


if __name__ == "__main__":
    main()
