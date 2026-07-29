"""语言无关的指标契约 v1 适配器。"""

from __future__ import annotations

from typing import Any, Iterable

from . import APP_VERSION, CONTRACT_VERSION, SERVICE_ID
from .errors import STABLE_ERROR_CODES


PROVIDER_IDS = {
    "systemCpu": "windows.system-cpu.psutil.v1",
    "memory": "windows.memory.global-status.v1",
    "volumes": "windows.volume.psutil.v1",
    "uptime": "windows.uptime.boot-time.v1",
    "processes": "windows.process.win32.v1",
    "sampler": "perfmonitor.sampler.fixed-period.v1",
}

SOURCE_IDS = {
    "systemCpu": "windows.system-cpu-times.v1",
    "memory": "windows.global-memory-status-ex.v1",
    "volumes": "windows.volume-api.v1",
    "uptime": "windows.boot-time.v1",
    "processCpu": "windows.get-process-times.v1",
    "processMemory": "windows.k32-process-memory-info.v1",
    "sampler": "perfmonitor.monotonic-scheduler.v1",
}

METRIC_UNITS = {
    "system.cpu.utilization.percent": "percent",
    "system.cpu.logical_processor.count": "count",
    "system.memory.utilization.percent": "percent",
    "system.memory.used.bytes": "byte",
    "system.memory.available.bytes": "byte",
    "system.memory.total.bytes": "byte",
    "system.volume.utilization.percent": "percent",
    "system.volume.used.bytes": "byte",
    "system.volume.free.bytes": "byte",
    "system.volume.total.bytes": "byte",
    "system.uptime.seconds": "second",
    "process.cpu.normalized.percent": "percent",
    "process.cpu.core_equivalent.percent": "percent",
    "process.memory.working_set.bytes": "byte",
    "process.memory.private.bytes": "byte",
    "sampler.interval.seconds": "second",
    "sampler.duration.milliseconds": "millisecond",
    "sampler.jitter.milliseconds": "millisecond",
    "sampler.missed_intervals.count": "count",
    "sampler.skipped_intervals.count": "count",
}

HISTORY_METRIC_TO_INTERNAL = {
    "system.cpu.utilization.percent": "cpu",
    "system.memory.utilization.percent": "mem",
}
INTERNAL_HISTORY_TO_METRIC = {
    value: key for key, value in HISTORY_METRIC_TO_INTERNAL.items()
}

ERROR_METRIC_IDS = {
    "cpu": "system.cpu.utilization.percent",
    "memory": "system.memory.utilization.percent",
    "disk": "system.volume.utilization.percent",
    "uptime": "system.uptime.seconds",
    "processes": "process.cpu.normalized.percent",
    "collector": None,
}
ERROR_GROUPS = {
    "cpu": "systemCpu",
    "memory": "memory",
    "disk": "volumes",
    "uptime": "uptime",
    "processes": "processes",
    "collector": None,
}


def _metric(metric_id: str, value: Any, source_id: str) -> dict[str, Any]:
    return {
        "value": value,
        "unit": METRIC_UNITS[metric_id],
        "sourceId": source_id,
    }


def _contract_errors(
    errors: Iterable[dict[str, Any]],
    group_name: str | None,
) -> list[dict[str, Any]]:
    result = []
    for issue in errors:
        metric_name = str(issue.get("metric", "collector"))
        if ERROR_GROUPS.get(metric_name) != group_name:
            continue
        code = str(issue.get("code", "provider_failure"))
        if code not in STABLE_ERROR_CODES:
            code = "provider_failure"
        result.append(
            {
                "errorCode": code,
                "metricId": ERROR_METRIC_IDS.get(metric_name),
            }
        )
    return result


def _availability(
    *,
    has_data: bool,
    errors: list[dict[str, Any]],
    partial: bool = False,
) -> str:
    if has_data:
        return "partial" if partial or errors else "available"
    codes = {error["errorCode"] for error in errors}
    for priority in ("access_denied", "not_supported", "timeout"):
        if priority in codes:
            return (
                "permission_denied"
                if priority == "access_denied"
                else priority
            )
    return "error" if errors else "unavailable"


def _coverage(
    status: str,
    *,
    enumerated: int | None = None,
    readable: int | None = None,
    skipped: int | None = None,
    skipped_by_reason: dict[str, int] | None = None,
) -> dict[str, Any]:
    result: dict[str, Any] = {"status": status}
    optional = {
        "enumerated": enumerated,
        "readable": readable,
        "skipped": skipped,
        "skippedByReason": skipped_by_reason,
    }
    result.update({key: value for key, value in optional.items() if value is not None})
    return result


def _group(
    *,
    name: str,
    observed_at: str | None,
    freshness: str,
    data: Any,
    errors: list[dict[str, Any]],
    coverage: dict[str, Any],
    partial: bool = False,
) -> dict[str, Any]:
    return {
        "providerId": PROVIDER_IDS[name],
        "observedAtUtc": observed_at,
        "availability": _availability(
            has_data=data is not None,
            errors=errors,
            partial=partial,
        ),
        "freshness": freshness,
        "coverage": coverage,
        "errors": errors,
        "data": data,
    }


def _summary_availability(legacy_health: dict[str, Any]) -> str:
    status = legacy_health.get(
        "availabilityStatus",
        legacy_health.get("status", "error"),
    )
    return {
        "ok": "available",
        "partial": "partial",
        "starting": "unavailable",
        "error": "error",
    }.get(str(status), "error")


def build_snapshot(legacy: dict[str, Any]) -> dict[str, Any]:
    """把 0.2 参考快照转换为公开的 v1 指标契约。"""

    overview = legacy.get("overview") or {}
    health = legacy.get("health") or {}
    errors = health.get("errors") or []
    observed = legacy.get("providerObservedAtUtc") or {}
    freshness = str(health.get("freshness", "warming_up"))

    cpu = overview.get("cpu")
    cpu_errors = _contract_errors(errors, "systemCpu")
    cpu_data = None
    if cpu is not None:
        cpu_data = {
            "metrics": {
                "system.cpu.utilization.percent": _metric(
                    "system.cpu.utilization.percent",
                    cpu.get("percent"),
                    SOURCE_IDS["systemCpu"],
                ),
                "system.cpu.logical_processor.count": _metric(
                    "system.cpu.logical_processor.count",
                    cpu.get("logicalCpuCount"),
                    SOURCE_IDS["systemCpu"],
                ),
            }
        }

    memory = overview.get("memory")
    memory_errors = _contract_errors(errors, "memory")
    memory_data = None
    if memory is not None:
        memory_data = {
            "metrics": {
                "system.memory.utilization.percent": _metric(
                    "system.memory.utilization.percent",
                    memory.get("percent"),
                    SOURCE_IDS["memory"],
                ),
                "system.memory.used.bytes": _metric(
                    "system.memory.used.bytes",
                    memory.get("usedBytes"),
                    SOURCE_IDS["memory"],
                ),
                "system.memory.available.bytes": _metric(
                    "system.memory.available.bytes",
                    memory.get("availableBytes"),
                    SOURCE_IDS["memory"],
                ),
                "system.memory.total.bytes": _metric(
                    "system.memory.total.bytes",
                    memory.get("totalBytes"),
                    SOURCE_IDS["memory"],
                ),
            }
        }

    volume_errors = _contract_errors(errors, "volumes")
    volumes = overview.get("disks") or []
    volume_data = [
        {
            "volumeId": str(volume.get("device") or volume.get("mountpoint")),
            "mountpoint": volume.get("mountpoint"),
            "fileSystem": volume.get("fileSystem"),
            "isSystem": bool(volume.get("isSystem")),
            "metrics": {
                "system.volume.utilization.percent": _metric(
                    "system.volume.utilization.percent",
                    volume.get("percent"),
                    SOURCE_IDS["volumes"],
                ),
                "system.volume.used.bytes": _metric(
                    "system.volume.used.bytes",
                    volume.get("usedBytes"),
                    SOURCE_IDS["volumes"],
                ),
                "system.volume.free.bytes": _metric(
                    "system.volume.free.bytes",
                    volume.get("freeBytes"),
                    SOURCE_IDS["volumes"],
                ),
                "system.volume.total.bytes": _metric(
                    "system.volume.total.bytes",
                    volume.get("totalBytes"),
                    SOURCE_IDS["volumes"],
                ),
            },
        }
        for volume in volumes
    ] or None

    uptime_value = overview.get("uptimeSeconds")
    uptime_errors = _contract_errors(errors, "uptime")
    uptime_data = None
    if uptime_value is not None:
        uptime_data = {
            "metrics": {
                "system.uptime.seconds": _metric(
                    "system.uptime.seconds",
                    uptime_value,
                    SOURCE_IDS["uptime"],
                )
            }
        }

    process_meta = legacy.get("processCollection") or {}
    process_errors = _contract_errors(errors, "processes")
    processes = legacy.get("processes") or []
    process_data: list[dict[str, Any]] | None = [
        {
            "identity": {
                "pid": process.get("pid"),
                "creationTimeTicks": process.get("creationTimeTicks"),
            },
            "name": process.get("name"),
            "cpuReady": bool(process.get("cpuReady")),
            "metrics": {
                "process.cpu.normalized.percent": _metric(
                    "process.cpu.normalized.percent",
                    process.get("cpuNormalizedPct"),
                    SOURCE_IDS["processCpu"],
                ),
                "process.cpu.core_equivalent.percent": _metric(
                    "process.cpu.core_equivalent.percent",
                    process.get("cpuCoreEquivalentPct"),
                    SOURCE_IDS["processCpu"],
                ),
                "process.memory.working_set.bytes": _metric(
                    "process.memory.working_set.bytes",
                    process.get("workingSetBytes"),
                    SOURCE_IDS["processMemory"],
                ),
                "process.memory.private.bytes": _metric(
                    "process.memory.private.bytes",
                    process.get("privateBytes"),
                    SOURCE_IDS["processMemory"],
                ),
            },
        }
        for process in processes
    ]
    if not process_data and (
        legacy.get("sequence", 0) == 0 or process_errors
    ):
        process_data = None
    enumerated = int(process_meta.get("enumerated", 0))
    skipped = int(process_meta.get("skipped", 0))
    process_coverage = _coverage(
        str(process_meta.get("status", "limited")),
        enumerated=enumerated,
        readable=max(0, enumerated - skipped),
        skipped=skipped,
        skipped_by_reason=(
            {"provider_failure": skipped} if skipped else {}
        ),
    )

    sample = legacy.get("sample") or {}
    sampler_data = None
    if legacy.get("sequence", 0) > 0:
        sampler_data = {
            "metrics": {
                "sampler.interval.seconds": _metric(
                    "sampler.interval.seconds",
                    sample.get("intervalSeconds"),
                    SOURCE_IDS["sampler"],
                ),
                "sampler.duration.milliseconds": _metric(
                    "sampler.duration.milliseconds",
                    sample.get("durationMs"),
                    SOURCE_IDS["sampler"],
                ),
                "sampler.jitter.milliseconds": _metric(
                    "sampler.jitter.milliseconds",
                    sample.get("jitterMs"),
                    SOURCE_IDS["sampler"],
                ),
                "sampler.missed_intervals.count": _metric(
                    "sampler.missed_intervals.count",
                    sample.get("missedIntervalsTotal"),
                    SOURCE_IDS["sampler"],
                ),
                "sampler.skipped_intervals.count": _metric(
                    "sampler.skipped_intervals.count",
                    sample.get("skippedIntervalsAfterSample"),
                    SOURCE_IDS["sampler"],
                ),
            }
        }

    groups = {
        "systemCpu": _group(
            name="systemCpu",
            observed_at=observed.get("systemCpu"),
            freshness=freshness,
            data=cpu_data,
            errors=cpu_errors,
            coverage=_coverage("complete" if cpu_data else "limited"),
        ),
        "memory": _group(
            name="memory",
            observed_at=observed.get("memory"),
            freshness=freshness,
            data=memory_data,
            errors=memory_errors,
            coverage=_coverage("complete" if memory_data else "limited"),
        ),
        "volumes": _group(
            name="volumes",
            observed_at=observed.get("volumes"),
            freshness=freshness,
            data=volume_data,
            errors=volume_errors,
            coverage=_coverage("complete" if volume_data else "limited"),
            partial=bool(volume_errors),
        ),
        "uptime": _group(
            name="uptime",
            observed_at=observed.get("uptime"),
            freshness=freshness,
            data=uptime_data,
            errors=uptime_errors,
            coverage=_coverage("complete" if uptime_data else "limited"),
        ),
        "processes": _group(
            name="processes",
            observed_at=observed.get("processes"),
            freshness=freshness,
            data=process_data,
            errors=process_errors,
            coverage=process_coverage,
            partial=skipped > 0,
        ),
        "sampler": _group(
            name="sampler",
            observed_at=legacy.get("completedAtUtc"),
            freshness=freshness,
            data=sampler_data,
            errors=_contract_errors(errors, None),
            coverage=_coverage("complete" if sampler_data else "limited"),
        ),
    }

    return {
        "contractVersion": CONTRACT_VERSION,
        "productVersion": APP_VERSION,
        "instanceId": legacy["instanceId"],
        "sequence": legacy["sequence"],
        "scheduledAtUtc": legacy.get("scheduledAtUtc"),
        "startedAtUtc": legacy.get("startedAtUtc"),
        "completedAtUtc": legacy.get("completedAtUtc")
        or legacy.get("collectedAt"),
        "dataAgeSeconds": health.get("ageSeconds"),
        "summary": {
            "availability": _summary_availability(health),
            "freshness": freshness,
        },
        "retention": {
            "historyWindowSeconds": legacy.get("historyWindowSeconds"),
            "historyPointLimit": legacy.get("historyPointLimit"),
        },
        "groups": groups,
    }


def build_history(
    legacy: dict[str, Any],
    requested_metric_ids: Iterable[str],
) -> dict[str, Any]:
    metric_ids = tuple(requested_metric_ids)
    points = []
    for point in legacy["points"]:
        metrics: dict[str, Any] = {}
        for metric_id in metric_ids:
            internal = HISTORY_METRIC_TO_INTERNAL[metric_id]
            stats = point["stats"][internal]
            metrics[metric_id] = {
                "unit": METRIC_UNITS[metric_id],
                "min": stats["min"],
                "max": stats["max"],
                "avg": stats["avg"],
                "last": stats["last"],
            }
        points.append(
            {
                "startEpochMs": point["startEpochMs"],
                "endEpochMs": point["endEpochMs"],
                "sequence": point["sequence"],
                "metrics": metrics,
            }
        )
    return {
        "contractVersion": CONTRACT_VERSION,
        "productVersion": APP_VERSION,
        "instanceId": legacy["instanceId"],
        "query": {
            "metricIds": list(metric_ids),
            "fromEpochMs": legacy["fromEpochMs"],
            "toEpochMs": legacy["toEpochMs"],
            "maxPoints": legacy.get("requestedMaxPoints"),
        },
        "sourcePointCount": legacy["sourcePointCount"],
        "pointCount": len(points),
        "downsampled": legacy["downsampled"],
        "points": points,
    }


def build_capabilities(legacy: dict[str, Any]) -> dict[str, Any]:
    period_ms = max(
        1,
        round(float(legacy["sampleIntervalSeconds"]) * 1_000),
    )
    return {
        "contractVersion": CONTRACT_VERSION,
        "productVersion": APP_VERSION,
        "instanceId": legacy["instanceId"],
        "groups": [
            {
                "groupId": name,
                "providerId": provider_id,
                "defaultPeriodMs": period_ms,
                "requiredPrivilege": "user",
                "costClass": "low" if name != "processes" else "medium",
            }
            for name, provider_id in PROVIDER_IDS.items()
        ],
        "history": {
            "metricIds": list(HISTORY_METRIC_TO_INTERNAL),
            "defaultMaxPoints": legacy["history"]["defaultMaxPoints"],
            "maxPoints": legacy["history"]["maxPoints"],
            "ramPointLimit": legacy["history"]["ramPointLimit"],
            "aggregations": legacy["history"]["aggregation"],
        },
        "endpoints": legacy["endpoints"],
        "stableErrorCodes": list(STABLE_ERROR_CODES),
    }


def build_health(snapshot: dict[str, Any]) -> dict[str, Any]:
    return {
        "contractVersion": CONTRACT_VERSION,
        "productVersion": APP_VERSION,
        "service": SERVICE_ID,
        "instanceId": snapshot["instanceId"],
        "sequence": snapshot["sequence"],
        "completedAtUtc": snapshot["completedAtUtc"],
        "summary": snapshot["summary"],
    }
