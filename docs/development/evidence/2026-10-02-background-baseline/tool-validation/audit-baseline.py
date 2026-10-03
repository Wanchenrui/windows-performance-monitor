"""Independent read-only raw-to-summary audit; no product or UI operations."""
import csv
import hashlib
import importlib.util
import json
import math
import sys
from collections import defaultdict
from pathlib import Path

REPO = Path(r"D:\GitHub\windows-performance-monitor")


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest().upper()


def close(actual, expected, name):
    if not math.isclose(float(actual), float(expected), rel_tol=1e-9, abs_tol=1e-9):
        raise AssertionError(f"{name}: {actual} != {expected}")


def rows(path):
    with path.open(encoding="utf-8-sig", newline="") as stream:
        return list(csv.DictReader(stream))


def audit(root):
    baseline_path = root / "baseline.json"
    baseline = json.loads(baseline_path.read_text(encoding="utf-8-sig"))
    assert baseline["completed"] is True
    assert baseline["hardwareProfile"] == "workerDeployed"
    assert baseline["workerDiscoveryErrors"] == 0
    assert baseline["windowStateMismatchSamples"] == 0
    assert not baseline.get("error")
    for item in baseline["inputArtifacts"]:
        assert sha(item["path"]) == item["sha256"], item["path"]
    for item in baseline["runningPayloadFiles"]:
        assert sha(root / "payload" / item["path"]) == item["sha256"], item["path"]
    for item in baseline["source"]["inputFileHashes"]:
        assert sha(REPO / item["path"]) == item["sha256"], item["path"]
    process_rows = rows(root / "process-resources.csv")
    product_rows = rows(root / "product-resources.csv")
    by_pid = defaultdict(list)
    by_time = defaultdict(list)
    records = {str(item["pid"]): item for item in baseline["processes"]}
    for row in process_rows:
        record = records[row["pid"]]
        assert row["kind"] == record["kind"]
        assert row["startedUtc"] == record["startedUtc"]
        by_pid[row["pid"]].append(row)
        by_time[row["elapsedSeconds"]].append(row)
    for pid, items in by_pid.items():
        items.sort(key=lambda item: float(item["elapsedSeconds"]))
        previous = None
        for item in items:
            cpu = float(item["totalCpuSeconds"])
            if previous is None:
                assert item["deltaCpuSeconds"] == ""
            else:
                close(item["deltaCpuSeconds"], cpu - previous, "per-process cumulative delta")
            previous = cpu
    logical = baseline["environment"]["logicalProcessors"]
    for row in product_rows:
        items = by_time[row["elapsedSeconds"]]
        for field in ("privateBytes", "workingSetBytes", "handleCount", "threadCount"):
            close(row[field], sum(float(item[field]) for item in items), "set " + field)
        close(row["activeProcesses"], len(items), "active process count")
        close(row["deltaCpuSeconds"], sum(float(item["deltaCpuSeconds"] or 0) for item in items), "set CPU sum")
        assert row["workerDiscoveryComplete"].lower() == "true"
        if baseline["mode"] == "desktop-hidden":
            assert row["desktopVisible"].lower() == "false"
        if row["cpuCoverageComplete"].lower() == "true":
            close(row["cpuCoreEquivalentPct"], 100 * float(row["deltaCpuSeconds"]) / float(row["intervalSeconds"]), "probe core CPU")
            close(row["cpuMachineNormalizedPct"], float(row["cpuCoreEquivalentPct"]) / logical, "probe normalized CPU")
        else:
            assert not row["cpuCoreEquivalentPct"] and not row["cpuMachineNormalizedPct"]
    params = baseline["parameters"]
    steady = [row for row in product_rows if row["phase"] == "measurement"]
    eligible = [row for row in steady if row["intervalStartSeconds"] and float(row["intervalStartSeconds"]) >= params["warmupSeconds"] and row["cpuCoverageComplete"].lower() == "true" and float(row["intervalSeconds"]) > 0]
    cpu = sum(float(row["deltaCpuSeconds"]) for row in eligible)
    covered = sum(float(row["intervalSeconds"]) for row in eligible)
    summary = baseline["summary"]
    expected = {
        "probeSamples": len(product_rows), "measurementSamples": len(steady), "cpuCompleteSamples": len(eligible),
        "observedCpuDeltaSeconds": cpu, "cpuCoveredSeconds": covered,
        "cpuCoverageFractionOfRequestedMeasurement": covered / params["durationSeconds"],
        "cpuCoreEquivalentMeanPct": 100 * cpu / covered,
        "cpuMachineNormalizedMeanPct": 100 * cpu / covered / logical,
        "maximumProbeIntervalSeconds": max(float(row["intervalSeconds"] or 0) for row in product_rows),
        "probeGapSamples": sum(float(row["intervalSeconds"] or 0) > 1.5 * params["probeIntervalMilliseconds"] / 1000 for row in product_rows),
    }
    for key, source in (("peakPrivateBytes", "privateBytes"), ("peakWorkingSetBytes", "workingSetBytes"), ("peakHandles", "handleCount"), ("peakThreads", "threadCount")):
        expected[key] = max(float(row[source]) for row in steady)
    for key, source in (("peakDbBytes", "dbBytes"), ("peakWalBytes", "walBytes")):
        expected[key] = max(float(row[source]) for row in product_rows)
    before = baseline["storageBeforeShutdown"]
    warm = baseline["storageAtFirstMeasurementProbe"]
    expected["databaseGrowthBeforeShutdownBytes"] = before["dbBytes"] - warm["dbBytes"]
    expected["walGrowthBeforeShutdownBytes"] = before["walBytes"] - warm["walBytes"]
    for key, value in expected.items():
        close(summary[key], value, "summary " + key)
    assert cpu > 0 and covered > 0
    for item in baseline["processes"]:
        assert not item.get("cleanupError") and not item.get("sampleError")
        assert item["cleanup"] in ("already exited", "terminated owned process")
        assert "exitCode" in item
        assert Path(item["path"]).is_relative_to(root / "payload")
        if item["kind"] == "agent":
            assert item["exitCode"] == 0 and item["rootProcess"] is True
        elif item["kind"] == "worker":
            assert item["rootProcess"] is False
            assert Path(item["path"]).parent.name == "provider-worker"
        elif item["kind"] == "desktop":
            assert item["arguments"] == ["--start-hidden"]
    spec = importlib.util.spec_from_file_location("history", REPO / "scripts/read_baseline_history.py")
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    assert helper.inspect_history(root / "data/history-v1.db") == baseline["history"]
    assert baseline["history"]["integrityCheck"] == "ok"
    return {"status": "passed", "boundary": "Independent CSV recomputation and original-file hashes; short development sample only", "run": root.name, "mode": baseline["mode"], "files": [{"name": name, "sha256": sha(root / name)} for name in ("baseline.json", "process-resources.csv", "product-resources.csv")], "recomputedSummary": expected, "processRows": len(process_rows), "processes": [{key: item[key] for key in ("kind", "pid", "cleanup", "exitCode")} for item in baseline["processes"]], "storageAfterShutdown": baseline["storageAfterShutdown"], "persistedSnapshots": baseline["history"]["persistedSnapshots"], "missingPersistedDeliveries": baseline["history"]["missingDeliverySequencesBetweenPersistedRows"], "productStartedByAudit": False}


if __name__ == "__main__":
    result = audit(Path(sys.argv[1]).resolve())
    Path(sys.argv[2]).write_text(json.dumps(result, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
    print(json.dumps(result, indent=2, ensure_ascii=False))
