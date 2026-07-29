"""跨语言稳定错误码；异常类型名不得成为公开契约。"""

from __future__ import annotations

import psutil


STABLE_ERROR_CODES = (
    "access_denied",
    "process_exited",
    "not_supported",
    "timeout",
    "invalid_data",
    "resource_exhausted",
    "provider_failure",
)


def stable_error_code(exc: BaseException) -> str:
    """把 Python/Win32 异常归一化为稳定机器标识。"""

    if isinstance(exc, (PermissionError, psutil.AccessDenied)):
        return "access_denied"
    if isinstance(exc, (ProcessLookupError, psutil.NoSuchProcess)):
        return "process_exited"
    if isinstance(exc, NotImplementedError):
        return "not_supported"
    if isinstance(exc, TimeoutError):
        return "timeout"
    if isinstance(exc, (TypeError, ValueError)):
        return "invalid_data"
    if isinstance(exc, MemoryError):
        return "resource_exhausted"
    if isinstance(exc, OSError) and getattr(exc, "winerror", None) == 5:
        return "access_denied"
    return "provider_failure"
