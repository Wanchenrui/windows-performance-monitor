"""Windows 进程计数器的轻量原生封装。

psutil 的公开 ``process_iter(attrs=...)`` 会对拒绝访问的进程回退到逐 PID
系统快照。在装有终端安全软件、进程数量较多的机器上，这种回退可能把一次
枚举放大到数百毫秒。本模块对每个 PID 只执行一次受限句柄查询；无权限的
进程明确计为 skipped，不进行昂贵回退。
"""

from __future__ import annotations

import ctypes
import ntpath
import sys
from dataclasses import dataclass
from ctypes import wintypes


WINDOWS_TICKS_PER_SECOND = 10_000_000.0
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000


@dataclass(frozen=True)
class ProcessCounters:
    create_time_ticks: int
    cpu_total_seconds: float
    working_set_bytes: int | None
    private_bytes: int | None
    name: str | None


if sys.platform == "win32":

    class FILETIME(ctypes.Structure):
        _fields_ = [
            ("dwLowDateTime", wintypes.DWORD),
            ("dwHighDateTime", wintypes.DWORD),
        ]


    class PROCESS_MEMORY_COUNTERS_EX(ctypes.Structure):
        _fields_ = [
            ("cb", wintypes.DWORD),
            ("PageFaultCount", wintypes.DWORD),
            ("PeakWorkingSetSize", ctypes.c_size_t),
            ("WorkingSetSize", ctypes.c_size_t),
            ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPagedPoolUsage", ctypes.c_size_t),
            ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
            ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
            ("PagefileUsage", ctypes.c_size_t),
            ("PeakPagefileUsage", ctypes.c_size_t),
            ("PrivateUsage", ctypes.c_size_t),
        ]


def _filetime_to_int(value: "FILETIME") -> int:
    return (int(value.dwHighDateTime) << 32) | int(value.dwLowDateTime)


class WindowsProcessProvider:
    """GetProcessTimes + K32GetProcessMemoryInfo 的常驻函数绑定。"""

    def __init__(self) -> None:
        if sys.platform != "win32":
            raise RuntimeError("WindowsProcessProvider 仅支持 Windows")

        kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel32.OpenProcess.argtypes = (
            wintypes.DWORD,
            wintypes.BOOL,
            wintypes.DWORD,
        )
        kernel32.OpenProcess.restype = wintypes.HANDLE
        kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        kernel32.CloseHandle.restype = wintypes.BOOL
        kernel32.GetProcessTimes.argtypes = (
            wintypes.HANDLE,
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
            ctypes.POINTER(FILETIME),
        )
        kernel32.GetProcessTimes.restype = wintypes.BOOL
        kernel32.QueryFullProcessImageNameW.argtypes = (
            wintypes.HANDLE,
            wintypes.DWORD,
            wintypes.LPWSTR,
            ctypes.POINTER(wintypes.DWORD),
        )
        kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
        kernel32.K32GetProcessMemoryInfo.argtypes = (
            wintypes.HANDLE,
            ctypes.POINTER(PROCESS_MEMORY_COUNTERS_EX),
            wintypes.DWORD,
        )
        kernel32.K32GetProcessMemoryInfo.restype = wintypes.BOOL
        self._kernel32 = kernel32

    def query(self, pid: int) -> ProcessCounters | None:
        """返回单进程计数器；权限不足或进程消失时返回 None。"""

        handle = self._kernel32.OpenProcess(
            PROCESS_QUERY_LIMITED_INFORMATION,
            False,
            int(pid),
        )
        if not handle:
            return None

        try:
            creation = FILETIME()
            exit_time = FILETIME()
            kernel = FILETIME()
            user = FILETIME()
            if not self._kernel32.GetProcessTimes(
                handle,
                ctypes.byref(creation),
                ctypes.byref(exit_time),
                ctypes.byref(kernel),
                ctypes.byref(user),
            ):
                return None

            memory = PROCESS_MEMORY_COUNTERS_EX()
            memory.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS_EX)
            has_memory = bool(
                self._kernel32.K32GetProcessMemoryInfo(
                    handle,
                    ctypes.byref(memory),
                    memory.cb,
                )
            )
            image_path = ctypes.create_unicode_buffer(32768)
            image_path_size = wintypes.DWORD(len(image_path))
            has_name = bool(
                self._kernel32.QueryFullProcessImageNameW(
                    handle,
                    0,
                    image_path,
                    ctypes.byref(image_path_size),
                )
            )
            process_name = (
                ntpath.basename(image_path.value) if has_name else None
            )
            cpu_ticks = _filetime_to_int(kernel) + _filetime_to_int(user)
            return ProcessCounters(
                create_time_ticks=_filetime_to_int(creation),
                cpu_total_seconds=cpu_ticks / WINDOWS_TICKS_PER_SECOND,
                working_set_bytes=(
                    int(memory.WorkingSetSize) if has_memory else None
                ),
                private_bytes=(
                    int(memory.PrivateUsage) if has_memory else None
                ),
                name=process_name,
            )
        finally:
            self._kernel32.CloseHandle(handle)
