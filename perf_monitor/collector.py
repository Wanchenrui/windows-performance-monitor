"""低扰动 Windows 性能指标采集器。

0.2.0 变更：移除每个采样周期创建 PowerShell/WMI 子进程的实现，改用
psutil 封装的 Windows 原生系统与进程 API。所有百分比、字节数和数据源都
在输出字段中明确标识，避免把多核累计 CPU 误当作整机百分比。
"""

from __future__ import annotations

import os
import time
from datetime import datetime, timezone
from typing import Any

import psutil

from .windows_process import WindowsProcessProvider
from .errors import stable_error_code


MIB = 1024 * 1024
GIB = 1024 * MIB


def calculate_process_cpu_percent(
    delta_cpu_seconds: float,
    elapsed_seconds: float,
    logical_cpu_count: int,
) -> tuple[float, float]:
    """返回（整机归一化百分比，核心等效百分比）。

    核心等效百分比 100% 表示占满一个逻辑处理器；整机归一化百分比使用：

        100% * ΔCPUTime / (Δt * N_logical)

    因而在正常计数误差范围内被限制为 0～100%。
    """

    if elapsed_seconds <= 0:
        raise ValueError("elapsed_seconds 必须大于 0")
    if logical_cpu_count <= 0:
        raise ValueError("logical_cpu_count 必须大于 0")

    safe_delta = max(0.0, float(delta_cpu_seconds))
    core_equivalent = 100.0 * safe_delta / elapsed_seconds
    normalized = core_equivalent / logical_cpu_count
    return min(100.0, max(0.0, normalized)), max(0.0, core_equivalent)


def _round_or_none(value: float | None, digits: int = 1) -> float | None:
    return None if value is None else round(float(value), digits)


class NativeWindowsCollector:
    """通过 psutil 的 Windows 后端采集一次一致的指标批次。"""

    def __init__(self, process_limit: int = 5) -> None:
        self.process_limit = process_limit
        self.logical_cpu_count = max(1, psutil.cpu_count(logical=True) or 1)
        self.system_drive = self._detect_system_drive()
        self._previous_process_cpu: dict[
            tuple[int, int], tuple[float, float]
        ] = {}
        self._process_provider = WindowsProcessProvider()

        # psutil 的非阻塞 CPU 百分比依赖相邻两次系统时间快照；第一次只做预热。
        psutil.cpu_percent(interval=None)

    @staticmethod
    def _detect_system_drive() -> str:
        system_root = os.environ.get("SystemRoot") or os.environ.get("WINDIR")
        drive = os.environ.get("SystemDrive")
        if not drive and system_root:
            drive = os.path.splitdrive(system_root)[0]
        if not drive:
            drive = os.path.splitdrive(os.path.abspath(os.sep))[0] or "C:"
        return drive.rstrip("\\/") + "\\"

    @staticmethod
    def _issue(metric: str, exc: BaseException) -> dict[str, str]:
        message = str(exc).strip() or exc.__class__.__name__
        return {
            "metric": metric,
            "code": stable_error_code(exc),
            "nativeCode": exc.__class__.__name__,
            "message": message[:300],
        }

    @staticmethod
    def _observed_at_utc() -> str:
        return datetime.now(timezone.utc).isoformat(timespec="milliseconds")

    def collect(self, sample_monotonic: float | None = None) -> dict[str, Any]:
        """采集一个批次；单项失败不会伪装成 0，也不会阻断其余指标。"""

        sample_monotonic = (
            time.monotonic() if sample_monotonic is None else sample_monotonic
        )
        errors: list[dict[str, str]] = []
        observed_at: dict[str, str] = {}
        overview: dict[str, Any] = {
            "cpu": None,
            "memory": None,
            "systemDisk": None,
            "disks": [],
            "uptimeSeconds": None,
        }

        try:
            overview["cpu"] = {
                "percent": round(float(psutil.cpu_percent(interval=None)), 1),
                "logicalCpuCount": self.logical_cpu_count,
                "source": "psutil.cpu_percent / Windows system CPU times",
            }
        except Exception as exc:  # pragma: no cover - 依赖操作系统异常注入验证
            errors.append(self._issue("cpu", exc))
        finally:
            observed_at["systemCpu"] = self._observed_at_utc()

        try:
            memory = psutil.virtual_memory()
            overview["memory"] = {
                "percent": round(float(memory.percent), 1),
                "usedBytes": int(memory.total - memory.available),
                "availableBytes": int(memory.available),
                "totalBytes": int(memory.total),
                "source": "psutil.virtual_memory / Windows GlobalMemoryStatusEx",
            }
        except Exception as exc:  # pragma: no cover - 依赖操作系统异常注入验证
            errors.append(self._issue("memory", exc))
        finally:
            observed_at["memory"] = self._observed_at_utc()

        try:
            disks = self._collect_disks()
            overview["disks"] = disks
            overview["systemDisk"] = next(
                (disk for disk in disks if disk["isSystem"]), None
            )
            if overview["systemDisk"] is None:
                raise RuntimeError(f"未找到系统盘 {self.system_drive}")
        except Exception as exc:
            errors.append(self._issue("disk", exc))
        finally:
            observed_at["volumes"] = self._observed_at_utc()

        try:
            overview["uptimeSeconds"] = max(
                0, int(time.time() - float(psutil.boot_time()))
            )
        except Exception as exc:  # pragma: no cover - 依赖操作系统异常注入验证
            errors.append(self._issue("uptime", exc))
        finally:
            observed_at["uptime"] = self._observed_at_utc()

        process_meta: dict[str, int] = {
            "enumerated": 0,
            "skipped": 0,
            "cpuReady": 0,
        }
        try:
            processes, process_meta = self._collect_processes(sample_monotonic)
        except Exception as exc:
            processes = []
            errors.append(self._issue("processes", exc))
        finally:
            observed_at["processes"] = self._observed_at_utc()

        available_groups = sum(
            (
                overview["cpu"] is not None,
                overview["memory"] is not None,
                overview["systemDisk"] is not None,
                overview["uptimeSeconds"] is not None,
                bool(processes),
            )
        )
        if available_groups == 0:
            status = "error"
        elif errors:
            status = "partial"
        else:
            status = "ok"

        return {
            "status": status,
            "errors": errors,
            "overview": overview,
            "processes": processes,
            "processCollection": process_meta,
            "providerObservedAtUtc": observed_at,
        }

    def _collect_disks(self) -> list[dict[str, Any]]:
        disks: list[dict[str, Any]] = []
        seen: set[str] = set()
        system_mount = os.path.normcase(os.path.abspath(self.system_drive))

        for partition in psutil.disk_partitions(all=False):
            mountpoint = partition.mountpoint
            normalized = os.path.normcase(os.path.abspath(mountpoint))
            if normalized in seen or "cdrom" in partition.opts.lower():
                continue
            seen.add(normalized)
            try:
                usage = psutil.disk_usage(mountpoint)
            except (OSError, psutil.Error):
                continue
            disks.append(
                {
                    "device": partition.device,
                    "mountpoint": mountpoint,
                    "fileSystem": partition.fstype,
                    "percent": round(float(usage.percent), 1),
                    "usedBytes": int(usage.used),
                    "freeBytes": int(usage.free),
                    "totalBytes": int(usage.total),
                    "isSystem": normalized == system_mount,
                    "source": "psutil.disk_usage / Windows volume API",
                }
            )
        return disks

    def _collect_processes(
        self, sample_monotonic: float
    ) -> tuple[list[dict[str, Any]], dict[str, int]]:
        current_cpu: dict[tuple[int, int], tuple[float, float]] = {}
        rows: list[dict[str, Any]] = []
        skipped = 0

        process_ids = psutil.pids()
        enumerated = len(process_ids)
        # PID 列表由 Windows EnumProcesses 批量取得；其余字段由轻量原生
        # Provider 查询，避免 psutil 对拒绝访问进程执行昂贵的逐 PID回退。
        for pid in process_ids:
            try:
                pid = int(pid)
                if pid == 0:
                    continue
                counters = self._process_provider.query(pid)
                if counters is None:
                    skipped += 1
                    continue
                process_name = counters.name or (
                    "System" if pid == 4 else f"PID {pid}"
                )

                key = (pid, counters.create_time_ticks)
                cpu_total = counters.cpu_total_seconds
                current_cpu[key] = (cpu_total, sample_monotonic)

                normalized: float | None = None
                core_equivalent: float | None = None
                previous = self._previous_process_cpu.get(key)
                if previous is not None:
                    previous_cpu, previous_monotonic = previous
                    elapsed = sample_monotonic - previous_monotonic
                    delta_cpu = cpu_total - previous_cpu
                    if elapsed > 0 and delta_cpu >= 0:
                        normalized, core_equivalent = calculate_process_cpu_percent(
                            delta_cpu,
                            elapsed,
                            self.logical_cpu_count,
                        )

                rows.append(
                    {
                        "pid": pid,
                        "creationTimeTicks": counters.create_time_ticks,
                        "name": process_name,
                        "cpuNormalizedPct": _round_or_none(normalized, 1),
                        "cpuCoreEquivalentPct": _round_or_none(
                            core_equivalent, 1
                        ),
                        "cpuReady": normalized is not None,
                        "workingSetBytes": counters.working_set_bytes,
                        "privateBytes": counters.private_bytes,
                        "memorySource": (
                            "Windows K32GetProcessMemoryInfo "
                            "(WorkingSetSize / PrivateUsage)"
                        ),
                        "cpuSource": "Windows GetProcessTimes",
                    }
                )
            except (KeyError, TypeError, ValueError, psutil.Error, OSError):
                skipped += 1

        self._previous_process_cpu = current_cpu
        rows.sort(
            key=lambda row: (
                bool(row["cpuReady"]),
                row["cpuNormalizedPct"]
                if row["cpuNormalizedPct"] is not None
                else -1.0,
                row["workingSetBytes"] or 0,
            ),
            reverse=True,
        )
        selected = rows[: self.process_limit]
        return selected, {
            "enumerated": enumerated,
            "skipped": skipped,
            "cpuReady": sum(1 for row in rows if row["cpuReady"]),
            "status": "limited" if skipped else "complete",
        }
