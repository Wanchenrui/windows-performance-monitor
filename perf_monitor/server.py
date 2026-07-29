"""仅监听回环地址的版本化本地 HTTP 接口。"""

from __future__ import annotations

import json
import logging
import socket
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any
from urllib.parse import parse_qs, urlsplit

from .contract_v1 import (
    HISTORY_METRIC_TO_INTERNAL,
    build_capabilities,
    build_health,
    build_history,
    build_snapshot,
)
from .store import DEFAULT_HISTORY_QUERY_MAX_POINTS


class LocalThreadingHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = False

    def server_bind(self) -> None:
        # Windows 默认的地址复用语义可能允许多个监听者竞争同一端口。
        # 独占绑定使端口冲突成为确定、可报告的启动错误。
        if hasattr(socket, "SO_EXCLUSIVEADDRUSE"):
            self.socket.setsockopt(
                socket.SOL_SOCKET,
                socket.SO_EXCLUSIVEADDRUSE,
                1,
            )
        super().server_bind()


class DashboardHandler(BaseHTTPRequestHandler):
    store: Any
    static_dir: Path
    logger = logging.getLogger(__name__)

    _STATIC_FILES = {
        "/": ("index.html", "text/html; charset=utf-8"),
        "/index.html": ("index.html", "text/html; charset=utf-8"),
        "/dashboard.css": ("dashboard.css", "text/css; charset=utf-8"),
        "/dashboard.js": (
            "dashboard.js",
            "application/javascript; charset=utf-8",
        ),
    }

    def _security_headers(self) -> None:
        self.send_header("Cache-Control", "no-store")
        self.send_header("X-Content-Type-Options", "nosniff")
        self.send_header("X-Frame-Options", "DENY")
        self.send_header("Referrer-Policy", "no-referrer")
        self.send_header(
            "Content-Security-Policy",
            "default-src 'self'; script-src 'self'; "
            "style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
            "connect-src 'self'; object-src 'none'; frame-ancestors 'none'",
        )

    def _send_bytes(
        self,
        status: int,
        body: bytes,
        content_type: str,
    ) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self._security_headers()
        self.end_headers()
        self.wfile.write(body)

    def _send_json(self, status: int, payload: dict[str, Any]) -> None:
        self._send_json_with_headers(status, payload)

    def _send_json_with_headers(
        self,
        status: int,
        payload: dict[str, Any],
        headers: dict[str, str] | None = None,
    ) -> None:
        body = json.dumps(
            payload,
            ensure_ascii=False,
            allow_nan=False,
            separators=(",", ":"),
        ).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        if headers:
            for name, value in headers.items():
                self.send_header(name, value)
        self._security_headers()
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self) -> None:
        request = urlsplit(self.path)
        path = request.path
        if path == "/api/v1/snapshot":
            self._send_json(200, build_snapshot(self.store.get_snapshot()))
            return
        if path == "/api/v1/history":
            self._serve_history(request.query)
            return
        if path == "/api/v1/capabilities":
            self._send_json(
                200,
                build_capabilities(self.store.get_capabilities()),
            )
            return
        if path == "/api/stats":
            self._send_json_with_headers(
                200,
                self.store.get_payload(),
                {
                    "Deprecation": "true",
                    "Link": '</api/v1/snapshot>; rel="successor-version"',
                },
            )
            return
        if path in ("/api/health", "/api/v1/health"):
            snapshot = build_snapshot(self.store.get_snapshot())
            self._send_json(200, build_health(snapshot))
            return
        if path in self._STATIC_FILES:
            filename, content_type = self._STATIC_FILES[path]
            try:
                body = (self.static_dir / filename).read_bytes()
            except OSError as exc:
                self.logger.error("读取静态资源失败 %s: %s", filename, exc)
                self._send_json(
                    500,
                    {
                        "error": "static_asset_unavailable",
                        "asset": filename,
                    },
                )
                return
            self._send_bytes(200, body, content_type)
            return

        self._send_json(404, {"error": "not_found"})

    def _serve_history(self, query: str) -> None:
        try:
            params = parse_qs(query, keep_blank_values=True)
            metrics_raw = params.get(
                "metrics",
                [",".join(HISTORY_METRIC_TO_INTERNAL)],
            )
            metric_ids = tuple(
                dict.fromkeys(
                    metric.strip()
                    for raw in metrics_raw
                    for metric in raw.split(",")
                    if metric.strip()
                )
            )
            unsupported = set(metric_ids) - set(HISTORY_METRIC_TO_INTERNAL)
            if unsupported:
                names = ", ".join(sorted(unsupported))
                raise ValueError(f"不支持的历史指标：{names}")
            internal_metrics = tuple(
                HISTORY_METRIC_TO_INTERNAL[metric_id]
                for metric_id in metric_ids
            )
            from_epoch_ms = self._optional_integer(params, "from")
            to_epoch_ms = self._optional_integer(params, "to")
            max_points = self._optional_integer(params, "maxPoints")
            if max_points is None:
                max_points = DEFAULT_HISTORY_QUERY_MAX_POINTS
            legacy = self.store.get_history(
                metrics=internal_metrics,
                from_epoch_ms=from_epoch_ms,
                to_epoch_ms=to_epoch_ms,
                max_points=max_points,
            )
            payload = build_history(legacy, metric_ids)
        except (TypeError, ValueError) as exc:
            self._send_json(
                400,
                {
                    "error": "invalid_query",
                    "message": str(exc),
                },
            )
            return
        self._send_json(200, payload)

    @staticmethod
    def _optional_integer(
        params: dict[str, list[str]],
        name: str,
    ) -> int | None:
        values = params.get(name)
        if not values:
            return None
        if len(values) != 1 or values[0] == "":
            raise ValueError(f"{name} 必须是单个整数")
        try:
            return int(values[0])
        except ValueError as exc:
            raise ValueError(f"{name} 必须是整数毫秒时间戳") from exc

    def log_message(self, fmt: str, *args: object) -> None:
        self.logger.debug("%s - %s", self.client_address[0], fmt % args)


def create_server(
    host: str,
    port: int,
    store: Any,
    static_dir: Path,
) -> LocalThreadingHTTPServer:
    """创建绑定指定快照存储的服务实例。"""

    class BoundDashboardHandler(DashboardHandler):
        pass

    BoundDashboardHandler.store = store
    BoundDashboardHandler.static_dir = static_dir
    return LocalThreadingHTTPServer((host, port), BoundDashboardHandler)
